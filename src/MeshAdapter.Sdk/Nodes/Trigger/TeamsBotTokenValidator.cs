using System.Collections.Concurrent;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Outcome of an inbound Bot Framework token validation. <see cref="Reason"/> is a short
/// operator-facing explanation for the log line on rejection — never token material.
/// </summary>
internal readonly record struct TeamsBotTokenOutcome(bool IsValid, string Reason)
{
    public static TeamsBotTokenOutcome Valid() => new(true, "ok");
    public static TeamsBotTokenOutcome Invalid(string reason) => new(false, reason);
}

/// <summary>
/// Validates the inbound Bot Framework JWT of a Teams activity — including the
/// cryptographic signature against the Bot Framework's published signing keys (AB#5010).
/// The previous check only decoded the payload and compared audience/expiry, which any
/// caller can forge; with this validator the messaging endpoint can be exposed publicly.
/// </summary>
/// <remarks>
/// Signing keys are resolved through <see cref="ConfigurationManager{T}"/> from the OpenID
/// metadata document (the node configuration's <c>OpenIdMetadataUrl</c>).
/// The manager caches per metadata URL process-wide and refreshes on its own schedule; on a
/// signature-key miss the validator requests one refresh and retries once — the standard
/// key-rollover handling, bounded so a flood of forged tokens cannot turn into a flood of
/// metadata requests.
/// </remarks>
internal class TeamsBotTokenValidator
{
    /// <summary>Issuer of Bot Framework channel-to-bot tokens (public cloud).</summary>
    internal const string BotFrameworkIssuer = "https://api.botframework.com";

    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        ConfigurationManagers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, CancellationToken, Task<OpenIdConnectConfiguration>> _getConfiguration;
    private readonly Action<string> _requestRefresh;

    public TeamsBotTokenValidator()
    {
        _getConfiguration = (metadataUrl, ct) => GetManager(metadataUrl).GetConfigurationAsync(ct);
        _requestRefresh = metadataUrl => GetManager(metadataUrl).RequestRefresh();
    }

    /// <summary>Test seam: supply the OpenID configuration (signing keys) directly.</summary>
    internal TeamsBotTokenValidator(
        Func<string, CancellationToken, Task<OpenIdConnectConfiguration>> getConfiguration,
        Action<string>? requestRefresh = null)
    {
        _getConfiguration = getConfiguration;
        _requestRefresh = requestRefresh ?? (_ => { });
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> GetManager(string metadataUrl) =>
        ConfigurationManagers.GetOrAdd(metadataUrl,
            url => new ConfigurationManager<OpenIdConnectConfiguration>(
                url, new OpenIdConnectConfigurationRetriever()));

    /// <summary>
    /// Validates the Authorization header of an inbound activity: Bearer scheme, signature
    /// against the metadata document's signing keys, issuer, audience (the bot App ID) and
    /// lifetime (5 minutes clock skew, matching the Bot Framework guidance).
    /// </summary>
    public async Task<TeamsBotTokenOutcome> ValidateAsync(
        string? authHeader,
        string expectedAudience,
        string metadataUrl,
        IReadOnlyCollection<string>? validIssuers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return TeamsBotTokenOutcome.Invalid("missing or non-Bearer Authorization header");
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var issuers = validIssuers is { Count: > 0 } ? validIssuers : [BotFrameworkIssuer];

        var outcome = await ValidateCoreAsync(token, expectedAudience, metadataUrl, issuers,
            cancellationToken);
        if (outcome.IsValid || !outcome.Reason.Contains("signature key", StringComparison.OrdinalIgnoreCase))
        {
            return outcome;
        }

        // Key not found: the metadata cache may predate a key rollover — refresh once and retry.
        _requestRefresh(metadataUrl);
        return await ValidateCoreAsync(token, expectedAudience, metadataUrl, issuers,
            cancellationToken);
    }

    private async Task<TeamsBotTokenOutcome> ValidateCoreAsync(
        string token,
        string expectedAudience,
        string metadataUrl,
        IReadOnlyCollection<string> validIssuers,
        CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _getConfiguration(metadataUrl, cancellationToken);
        }
        catch (Exception e)
        {
            // Fail closed: without the signing keys nothing can be verified.
            return TeamsBotTokenOutcome.Invalid($"signing key metadata unavailable ({e.GetType().Name})");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuers = validIssuers,
            ValidAudience = expectedAudience,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        if (result.IsValid)
        {
            return TeamsBotTokenOutcome.Valid();
        }

        return result.Exception switch
        {
            SecurityTokenSignatureKeyNotFoundException => TeamsBotTokenOutcome.Invalid("signature key not found"),
            SecurityTokenInvalidSignatureException => TeamsBotTokenOutcome.Invalid("invalid signature"),
            SecurityTokenInvalidAudienceException => TeamsBotTokenOutcome.Invalid("invalid audience"),
            SecurityTokenInvalidIssuerException => TeamsBotTokenOutcome.Invalid("invalid issuer"),
            SecurityTokenExpiredException => TeamsBotTokenOutcome.Invalid("token expired"),
            SecurityTokenInvalidLifetimeException => TeamsBotTokenOutcome.Invalid("invalid lifetime"),
            null => TeamsBotTokenOutcome.Invalid("token rejected"),
            var e => TeamsBotTokenOutcome.Invalid($"token rejected ({e.GetType().Name})"),
        };
    }
}

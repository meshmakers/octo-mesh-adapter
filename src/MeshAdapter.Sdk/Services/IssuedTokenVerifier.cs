using Duende.IdentityModel.Client;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Verifies a token the adapter's own identity service issued in answer to a request of the adapter, before any of
///     its claims is used (AB#9776; shared with the cross-tenant identity proof of AB#9767).
/// </summary>
/// <remarks>
///     Invariants every caller relies on — a token passes only when all hold:
///     <list type="number">
///         <item>it is a JWS whose signature verifies against the signing keys (<c>jwks_uri</c>) of the discovery
///         document the request was made with — the own issuer's keys, never keys named by the token — with an
///         asymmetric algorithm (<see cref="AcceptedSigningAlgorithms" />; <c>none</c> and <c>HS*</c> are
///         refused);</item>
///         <item>its <c>iss</c> is one of the adapter's own names (<see cref="OwnIssuerPolicy" />);</item>
///         <item>its <c>tenant_id</c> equals the tenant the request was made for (case-insensitive);</item>
///         <item>it carries an <c>exp</c> that is not past (one minute clock skew) — for every caller, the service-account
///         tokens included: a stale or replayed token never becomes an identity.</item>
///     </list>
///     A failed check returns <c>null</c> after one log line; the token itself is never logged.
/// </remarks>
internal sealed class IssuedTokenVerifier
{
    /// <summary>
    ///     Signature algorithms an issued token may carry (AB#9776): the asymmetric ones only, the same list
    ///     <c>CkMethodTokenOptions.DefaultAlgorithms</c> of octo-common-services accepts. The identity service signs
    ///     with the key of its configured certificate (RS256 for an RSA certificate, ES* for an ECDSA one), so the
    ///     list is not narrowed to RS256; <c>none</c> and every symmetric <c>HS*</c> algorithm are refused, whatever
    ///     keys the discovery document publishes.
    /// </summary>
    internal static readonly string[] AcceptedSigningAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
    ];

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly OwnIssuerPolicy _ownIssuer;
    private readonly ILogger _logger;

    public IssuedTokenVerifier(OwnIssuerPolicy ownIssuer, ILogger logger)
    {
        _ownIssuer = ownIssuer;
        _logger = logger;
    }

    /// <summary>Verifies <paramref name="accessToken" />; see the class remarks.</summary>
    /// <param name="accessToken">The issued token. Credential material, never logged.</param>
    /// <param name="discovery">The discovery document of the own issuer the token was requested from.</param>
    /// <param name="tenantId">The tenant the token must be issued for.</param>
    /// <param name="purpose">Names the request in log lines, e.g. <c>service account configuration 'x'</c>.</param>
    /// <returns>The claims of the verified token, or <c>null</c>.</returns>
    public async Task<JwtPayloadReader.JwtClaims?> VerifyAsync(string accessToken, DiscoveryDocumentResponse discovery,
        string? tenantId, string purpose)
    {
        var rawKeySet = discovery.KeySet?.RawData;
        if (string.IsNullOrWhiteSpace(rawKeySet))
        {
            _logger.LogError(
                "The token issued for {Purpose} cannot be verified: the identity service published no signing keys (jwks_uri); the token is refused (AB#9776)",
                purpose);
            return null;
        }

        TokenValidationResult result;
        try
        {
            var signingKeys = new JsonWebKeySet(rawKeySet).GetSigningKeys();
            result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken,
                new TokenValidationParameters
                {
                    IssuerSigningKeys = signingKeys,
                    RequireSignedTokens = true,
                    ValidAlgorithms = AcceptedSigningAlgorithms,
                    TryAllIssuerSigningKeys = true,
                    ValidateIssuerSigningKey = false,
                    // Compared below with the own-issuer normalization (trailing slash, case of host).
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = ClockSkew
                });
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException
                                       or SecurityTokenException)
        {
            _logger.LogError(
                "The token issued for {Purpose} cannot be verified ({Reason}); the token is refused (AB#9776)",
                purpose, ex.GetType().Name);
            return null;
        }

        if (!result.IsValid)
        {
            _logger.LogError(
                "The token issued for {Purpose} failed signature or lifetime verification ({Reason}); the token is refused (AB#9776)",
                purpose, result.Exception?.GetType().Name ?? "invalid");
            return null;
        }

        if (!JwtPayloadReader.TryRead(accessToken, out var claims))
        {
            _logger.LogError("The token issued for {Purpose} is not a readable JWT; the token is refused", purpose);
            return null;
        }

        if (!_ownIssuer.IsOwnIssuer(claims.Issuer))
        {
            _logger.LogError(
                "The token issued for {Purpose} names the issuer '{Issuer}', which is not this adapter's own identity service; the token is refused (AB#9776)",
                purpose, claims.Issuer ?? "(none)");
            return null;
        }

        if (string.IsNullOrWhiteSpace(tenantId)
            || !string.Equals(claims.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "The token issued for {Purpose} is for tenant '{TokenTenantId}', not for the requested tenant '{TenantId}'; the token is refused (AB#9776)",
                purpose, claims.TenantId ?? "(none)", tenantId ?? "(unknown)");
            return null;
        }

        return claims;
    }
}

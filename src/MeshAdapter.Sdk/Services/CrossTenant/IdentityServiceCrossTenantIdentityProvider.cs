using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>
///     Default <see cref="ICrossTenantIdentityProvider" />: asks the adapter's own identity service for a credential
///     issued for the target tenant.
/// </summary>
/// <remarks>
///     <para>
///         <b>User caller:</b> RFC 8693 token exchange of the caller's access token with <c>acr_values=tenant:U</c>,
///         authenticated with the adapter's own client at the adapter's own issuer (never another issuer: the adapter
///         sends its client secret only there). The identity service answers with a token for the target's shadow
///         user whose roles come only from provisioning in the target; it applies grants, allow-lists, second factor,
///         lockout and the generation of the source token. Refusals come back as OAuth errors and yield <c>null</c>.
///     </para>
///     <para>
///         <b>Service account:</b> the identity service's exchange for service-account subjects is not wired yet, so
///         this path refuses (<c>null</c>). It never falls back to the account's own token, which is a token of the
///         source tenant.
///     </para>
///     <para>
///         The issued token is read with <see cref="JwtPayloadReader" /> and its <c>iss</c> must be one of the
///         adapter's own names (<see cref="OwnIssuerPolicy" />, AB#9776). Its signature is not verified here: the token
///         comes straight from the own issuer's token endpoint over TLS, in answer to a request this class made. It is
///         not returned, cached or logged; only subject, roles, tenant and generation leave this class.
///     </para>
/// </remarks>
internal sealed class IdentityServiceCrossTenantIdentityProvider : ICrossTenantIdentityProvider
{
    /// <summary>RFC 8693 token-exchange grant type.</summary>
    internal const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>RFC 8693 token type of an access token.</summary>
    internal const string AccessTokenTypeIdentifier = "urn:ietf:params:oauth:token-type:access_token";

    private static readonly HttpClient SharedTokenHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly AdapterOptions _adapterOptions;
    private readonly OwnIssuerPolicy _ownIssuer;
    private readonly ILogger<IdentityServiceCrossTenantIdentityProvider> _logger;

    public IdentityServiceCrossTenantIdentityProvider(IOptions<AdapterOptions> adapterOptions,
        IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
        ILogger<IdentityServiceCrossTenantIdentityProvider> logger)
        : this(SharedTokenHttpClient, adapterOptions.Value, meshAdapterConfiguration.Value, logger)
    {
    }

    /// <summary>Test seam: a scripted token endpoint.</summary>
    internal IdentityServiceCrossTenantIdentityProvider(HttpClient httpClient, AdapterOptions adapterOptions,
        MeshAdapterConfiguration meshAdapterConfiguration, ILogger<IdentityServiceCrossTenantIdentityProvider> logger)
    {
        _httpClient = httpClient;
        _adapterOptions = adapterOptions;
        _ownIssuer = new OwnIssuerPolicy(adapterOptions, meshAdapterConfiguration);
        _logger = logger;
    }

    public async Task<CrossTenantProof?> ProveAsync(CrossTenantProofRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SubjectKind != CrossTenantSubjectKind.User)
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Cross-tenant proof for a service account to '{TargetTenantId}' is not available in this adapter version; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CallerAccessToken))
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Cross-tenant proof to '{TargetTenantId}' requested for a caller without an access token; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_adapterOptions.ClientId) || string.IsNullOrWhiteSpace(_adapterOptions.ClientSecret))
        {
            // Not a refusal of the identity service but a missing precondition of this adapter: fail closed.
            throw new CrossTenantIdentityUnavailableException(
                "The adapter has no own client credentials (Adapter:ClientId/ClientSecret) for the token exchange.");
        }

        // Own issuer only (AB#9776, the same policy as the service-account token path): Adapter:IssuerUri, else the
        // bearer-validation authority. Never a configured foreign issuer.
        var issuerUri = _ownIssuer.Endpoint;
        if (string.IsNullOrWhiteSpace(issuerUri))
        {
            throw new CrossTenantIdentityUnavailableException(
                "The adapter has no issuer (Adapter:IssuerUri or Adapter:AuthorityUrl) for the token exchange.");
        }

        TokenResponse response;
        try
        {
            var discovery = await _httpClient.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest
            {
                Address = issuerUri,
                // Split-horizon installations: see ServiceAccountTokenService.CreateDiscoveryRequest.
                Policy = { ValidateIssuerName = false, ValidateEndpoints = false }
            }, cancellationToken);
            if (discovery.IsError)
            {
                throw new CrossTenantIdentityUnavailableException(
                    $"OIDC discovery at the adapter's issuer failed ({discovery.ErrorType}).");
            }

            response = await _httpClient.RequestTokenAsync(new TokenRequest
            {
                Address = discovery.TokenEndpoint,
                GrantType = TokenExchangeGrantType,
                ClientId = _adapterOptions.ClientId,
                ClientSecret = _adapterOptions.ClientSecret,
                Parameters =
                {
                    { OidcConstants.TokenRequest.SubjectToken, request.CallerAccessToken },
                    { OidcConstants.TokenRequest.SubjectTokenType, AccessTokenTypeIdentifier },
                    { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{request.TargetTenantId}" },
                    {
                        OidcConstants.TokenRequest.Scope,
                        CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                    }
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CrossTenantIdentityUnavailableException)
        {
            // The exception text of an HTTP client never carries the request body here, but it is not passed on anyway.
            throw new CrossTenantIdentityUnavailableException(
                $"The token exchange at the adapter's issuer could not be performed ({ex.GetType().Name}).");
        }

        if (response.IsError)
        {
            if (response.ErrorType == ResponseErrorType.Protocol &&
                (int)response.HttpStatusCode is >= 400 and < 500)
            {
                // An OAuth refusal (invalid_grant, access_denied with tenant_not_provisioned / grant_not_permitted,
                // unauthorized_client, invalid_target): the identity service said no. Only the error code is logged.
                _logger.LogWarning(
                    "[{SourceTenantId}] Identity service refused the cross-tenant exchange to '{TargetTenantId}': {Error}",
                    request.SourceTenantId, request.TargetTenantId, response.Error);
                return null;
            }

            throw new CrossTenantIdentityUnavailableException(
                $"The token exchange at the adapter's issuer failed ({response.ErrorType}, HTTP {(int)response.HttpStatusCode}).");
        }

        if (!TryReadClaims(response.AccessToken, out var claims))
        {
            throw new CrossTenantIdentityUnavailableException("The exchanged token could not be read.");
        }

        if (!_ownIssuer.IsOwnIssuer(claims.Issuer))
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Exchanged token for '{TargetTenantId}' names the issuer '{Issuer}', which is not this adapter's own identity service; refused",
                request.SourceTenantId, request.TargetTenantId, claims.Issuer ?? "(none)");
            return null;
        }

        if (string.IsNullOrWhiteSpace(claims.Subject) || string.IsNullOrWhiteSpace(claims.TenantId))
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Exchanged token for '{TargetTenantId}' carries no subject or tenant; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        return new CrossTenantProof(claims.TenantId, claims.Generation,
            RtSecurityContext.ForUser(claims.Subject, claims.Roles));
    }

    internal sealed record ExchangedTokenClaims(string? Subject, string? TenantId, string? Generation,
        IReadOnlyList<string> Roles, string? Issuer);

    /// <summary>
    ///     Reads subject, tenant, generation, roles and issuer of an issued token (payload only), with the reader the
    ///     service-account token path uses (<see cref="JwtPayloadReader" />).
    /// </summary>
    internal static bool TryReadClaims(string? token, out ExchangedTokenClaims claims)
    {
        claims = new ExchangedTokenClaims(null, null, null, [], null);
        if (!JwtPayloadReader.TryRead(token, out var jwt))
        {
            return false;
        }

        claims = new ExchangedTokenClaims(jwt.Subject, jwt.TenantId, jwt.TenantGeneration, jwt.Roles, jwt.Issuer);
        return true;
    }
}

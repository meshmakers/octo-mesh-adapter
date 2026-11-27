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
///     issued for the target tenant (RFC 8693 token exchange, <c>acr_values=tenant:U</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>User caller:</b> the caller's access token is the subject, presented by the adapter's own client (it
///         holds the token-exchange grant). Identity answers with a token for the target's shadow user whose roles
///         come only from provisioning in the target; it applies grants, allow-lists, second factor, lockout and the
///         generation of the source token.
///     </para>
///     <para>
///         <b>Service account</b> (AB#9777 contract): the subject is the pipeline service account's own
///         client-credentials token of the source tenant T (own issuer, verified,
///         <see cref="IServiceAccountSubjectTokenSource" />), presented by U's public shadow client
///         <c>xt_{T}_{clientId}</c> without a secret. Identity checks the relation, T's allow-list, the mirror flag and
///         the generation, and issues a client-credentials-shaped token of the shadow client.
///     </para>
///     <para>
///         Invariants of the issued token, checked before any claim is used (all fail closed, <c>null</c>):
///     </para>
///     <list type="bullet">
///         <item>signature against the own issuer's JWKS, <c>iss</c> one of the own names, not expired, and
///         <c>tenant_id</c> == U (<see cref="IssuedTokenVerifier" />, the verifier of the AB#9776 fix);</item>
///         <item>user: a <c>sub</c> (the shadow user);</item>
///         <item>service account: <c>client_id</c> == <c>xt_{T}_{clientId}</c> (exact, ordinal) and no <c>sub</c>, so a
///         token for another presenter or a user token is never taken for the account's right;</item>
///         <item>only the token's roles reach the proof — never the source identity's roles.</item>
///     </list>
///     <para>
///         <c>tenant_gen</c> is returned with the proof and compared with U's current generation by the guard.
///         OAuth refusals (<c>access_denied</c>, <c>unauthorized_client</c>, <c>invalid_grant</c>) yield <c>null</c>, which the guard
///         turns into <c>CROSS_TENANT_FORBIDDEN</c>; only the error code and a known reason keyword are logged. No
///         token, secret or error text of the identity service leaves this class.
///     </para>
/// </remarks>
internal sealed class IdentityServiceCrossTenantIdentityProvider : ICrossTenantIdentityProvider
{
    /// <summary>RFC 8693 token-exchange grant type.</summary>
    internal const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>RFC 8693 token type of an access token.</summary>
    internal const string AccessTokenTypeIdentifier = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>Prefix of the shadow client in the target tenant (identity's naming, AB#9777).</summary>
    internal const string ShadowClientPrefix = "xt_";

    /// <summary>The reason keywords identity puts at the start of <c>error_description</c> (AB#9777); logged, nothing else.</summary>
    private static readonly string[] KnownRefusalReasons =
    [
        "tenant_not_provisioned", "grant_not_permitted", "mirrored_client", "ambiguous_subject",
        "shadow_client_invalid", "cross-tenant access", "a service-account subject"
    ];

    private static readonly HttpClient SharedTokenHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly AdapterOptions _adapterOptions;
    private readonly OwnIssuerPolicy _ownIssuer;
    private readonly IssuedTokenVerifier _verifier;
    private readonly IServiceAccountSubjectTokenSource? _subjectTokenSource;
    private readonly ILogger<IdentityServiceCrossTenantIdentityProvider> _logger;

    public IdentityServiceCrossTenantIdentityProvider(IOptions<AdapterOptions> adapterOptions,
        IOptions<MeshAdapterConfiguration> meshAdapterConfiguration, IServiceAccountTokenService serviceAccountTokenService,
        ILogger<IdentityServiceCrossTenantIdentityProvider> logger)
        : this(SharedTokenHttpClient, adapterOptions.Value, meshAdapterConfiguration.Value,
            // A replaced token service without the subject-token path cannot prove a service account: refused.
            serviceAccountTokenService as IServiceAccountSubjectTokenSource, logger)
    {
    }

    /// <summary>Test seam: a scripted token endpoint and subject-token source.</summary>
    internal IdentityServiceCrossTenantIdentityProvider(HttpClient httpClient, AdapterOptions adapterOptions,
        MeshAdapterConfiguration meshAdapterConfiguration, IServiceAccountSubjectTokenSource? subjectTokenSource,
        ILogger<IdentityServiceCrossTenantIdentityProvider> logger)
    {
        _httpClient = httpClient;
        _adapterOptions = adapterOptions;
        _ownIssuer = new OwnIssuerPolicy(adapterOptions, meshAdapterConfiguration);
        _verifier = new IssuedTokenVerifier(_ownIssuer, logger);
        _subjectTokenSource = subjectTokenSource;
        _logger = logger;
    }

    /// <summary>The shadow client of a service account of <paramref name="sourceTenantId" /> in a related tenant.</summary>
    internal static string ShadowClientId(string sourceTenantId, string clientId) =>
        $"{ShadowClientPrefix}{sourceTenantId}_{clientId}";

    public Task<CrossTenantProof?> ProveAsync(CrossTenantProofRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.SubjectKind switch
        {
            CrossTenantSubjectKind.User => ProveUserAsync(request, cancellationToken),
            CrossTenantSubjectKind.ServiceAccount => ProveServiceAccountAsync(request, cancellationToken),
            _ => Task.FromResult<CrossTenantProof?>(null)
        };
    }

    private async Task<CrossTenantProof?> ProveUserAsync(CrossTenantProofRequest request,
        CancellationToken cancellationToken)
    {
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

        var claims = await ExchangeAsync(request, request.CallerAccessToken, _adapterOptions.ClientId,
            _adapterOptions.ClientSecret, cancellationToken);
        if (claims == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(claims.Subject))
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Exchanged token for '{TargetTenantId}' carries no subject; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        return new CrossTenantProof(claims.TenantId!, claims.TenantGeneration,
            RtSecurityContext.ForUser(claims.Subject, claims.Roles));
    }

    private async Task<CrossTenantProof?> ProveServiceAccountAsync(CrossTenantProofRequest request,
        CancellationToken cancellationToken)
    {
        if (_subjectTokenSource == null || request.GlobalConfiguration == null)
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Cross-tenant proof for a service account to '{TargetTenantId}' has no service-account configuration or token source; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        ServiceAccountCredentials? credentials;
        try
        {
            // The same reader as the identity resolver, so the proof is for the account the execution acts as.
            credentials = ServiceAccountConfigurationReader.TryRead(request.GlobalConfiguration,
                request.SourceTenantId, _logger);
        }
        catch (MeshAdapterPipelineExecutionException)
        {
            // A configuration naming another tenant (AB#9776): already logged by the reader.
            return null;
        }

        if (credentials == null)
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Cross-tenant proof for a service account to '{TargetTenantId}': the pipeline has no service account; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        var subjectToken = await _subjectTokenSource.AcquireSubjectTokenAsync(credentials, cancellationToken);
        if (string.IsNullOrEmpty(subjectToken))
        {
            // The cause (identity service unreachable, account refused, foreign issuer) is logged by the token
            // service. None of them is about the target: the account has no token in its own tenant, so the target
            // cannot be verified, which fails a consolidation also with onRefused: Report (an outage must not drop
            // every company silently).
            throw new CrossTenantIdentityUnavailableException(
                $"No token of the pipeline's service account (client {credentials.ClientId}) in its own tenant.");
        }

        // The presenter is U's public shadow client; it has no secret (AB#9777 provisioning rule).
        var shadowClientId = ShadowClientId(request.SourceTenantId, credentials.ClientId);
        var claims = await ExchangeAsync(request, subjectToken, shadowClientId, clientSecret: null,
            cancellationToken);
        if (claims == null)
        {
            return null;
        }

        if (!string.Equals(claims.ClientId, shadowClientId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Exchanged token for '{TargetTenantId}' names the client '{TokenClientId}', not the shadow client '{ShadowClientId}'; refused",
                request.SourceTenantId, request.TargetTenantId, claims.ClientId ?? "(none)", shadowClientId);
            return null;
        }

        if (!string.IsNullOrEmpty(claims.Subject))
        {
            // A service token has no sub; a token with one is a user token and never the account's right.
            _logger.LogWarning(
                "[{SourceTenantId}] Exchanged token for service account to '{TargetTenantId}' carries a subject; refused",
                request.SourceTenantId, request.TargetTenantId);
            return null;
        }

        // Same subject rule as every client-credentials identity of the adapter: client_id stamps RtCreatedBy.
        return new CrossTenantProof(claims.TenantId!, claims.TenantGeneration,
            RtSecurityContext.ForUser(shadowClientId, claims.Roles));
    }

    /// <summary>
    ///     Performs the exchange at the own issuer and verifies the issued token (signature, issuer, lifetime, tenant).
    ///     Returns <c>null</c> on a refusal or a failed check; throws <see cref="CrossTenantIdentityUnavailableException" />
    ///     when identity cannot be asked.
    /// </summary>
    private async Task<JwtPayloadReader.JwtClaims?> ExchangeAsync(
        CrossTenantProofRequest request, string subjectToken, string clientId, string? clientSecret,
        CancellationToken cancellationToken)
    {
        // Own issuer only (AB#9776): Adapter:IssuerUri, else the bearer-validation authority. Never a foreign issuer,
        // so neither the subject token nor the adapter's secret reach another server.
        var issuerUri = _ownIssuer.Endpoint;
        if (string.IsNullOrWhiteSpace(issuerUri))
        {
            throw new CrossTenantIdentityUnavailableException(
                "The adapter has no issuer (Adapter:IssuerUri or Adapter:AuthorityUrl) for the token exchange.");
        }

        var purpose = $"the cross-tenant exchange {request.SourceTenantId} -> {request.TargetTenantId} ({request.SubjectKind})";

        DiscoveryDocumentResponse discovery;
        TokenResponse response;
        try
        {
            discovery = await _httpClient.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest
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

            var tokenRequest = new TokenRequest
            {
                Address = discovery.TokenEndpoint,
                GrantType = TokenExchangeGrantType,
                ClientId = clientId,
                Parameters =
                {
                    { OidcConstants.TokenRequest.SubjectToken, subjectToken },
                    { OidcConstants.TokenRequest.SubjectTokenType, AccessTokenTypeIdentifier },
                    { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{request.TargetTenantId}" },
                    {
                        OidcConstants.TokenRequest.Scope,
                        CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                    }
                }
            };
            if (clientSecret != null)
            {
                tokenRequest.ClientSecret = clientSecret;
            }
            else
            {
                // A public client: client_id in the body, no Authorization header.
                tokenRequest.ClientCredentialStyle = ClientCredentialStyle.PostBody;
            }

            response = await _httpClient.RequestTokenAsync(tokenRequest, cancellationToken);
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
                // An OAuth refusal: the identity service said no. Only the error code and a known reason keyword are
                // logged, never the description text.
                _logger.LogWarning(
                    "[{SourceTenantId}] Identity service refused the cross-tenant exchange to '{TargetTenantId}' ({SubjectKind}): {Error} ({RefusalReason})",
                    request.SourceTenantId, request.TargetTenantId, request.SubjectKind, response.Error,
                    RefusalReasonOf(response.ErrorDescription));
                return null;
            }

            throw new CrossTenantIdentityUnavailableException(
                $"The token exchange at the adapter's issuer failed ({response.ErrorType}, HTTP {(int)response.HttpStatusCode}).");
        }

        if (string.IsNullOrEmpty(response.AccessToken))
        {
            throw new CrossTenantIdentityUnavailableException("The token exchange returned no access token.");
        }

        var claims = await _verifier.VerifyAsync(response.AccessToken, discovery, request.TargetTenantId, purpose);
        return claims;
    }

    /// <summary>The known reason keyword at the start of an <c>error_description</c>, else <c>(none)</c>.</summary>
    internal static string RefusalReasonOf(string? errorDescription)
    {
        if (string.IsNullOrWhiteSpace(errorDescription))
        {
            return "(none)";
        }

        var trimmed = errorDescription.TrimStart();
        return KnownRefusalReasons.FirstOrDefault(r => trimmed.StartsWith(r, StringComparison.Ordinal)) ?? "(none)";
    }
}

using IdentityModel;
using IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Acquires and manages OAuth2 access tokens from a ServiceAccountConfiguration entity.
/// Reads client credentials from the runtime repository and uses them to obtain tokens
/// via the client credentials grant.
/// </summary>
public interface IServiceAccountTokenService
{
    /// <summary>
    /// Ensures a valid access token is available. Acquires a new token if needed.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    Task EnsureTokenAsync(ITenantRepository tenantRepository, string wellKnownName);

    /// <summary>
    ///     Acquires a <b>delegated</b> access token that runs on the end user's <c>sub</c> — the
    ///     service account of <paramref name="wellKnownName" /> acting on behalf of the caller who
    ///     presented <paramref name="subjectToken" /> (OctoMesh delegation grant, AB#5026/AB#5031).
    ///     The issued token carries the <b>intersection</b> of the service account's and the user's
    ///     roles plus an <c>act</c> claim naming the service account, so a downstream service applies
    ///     the user's own permissions instead of the service account's full reach.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    /// <param name="subjectToken">
    ///     The end user's raw access token, presented as <c>subject_token</c>. Credential material —
    ///     never log it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The delegated access token, or <c>null</c> when it could not be acquired.</returns>
    /// <remarks>
    ///     🔴 <b>The token is RETURNED, not written into <see cref="IServiceClientAccessToken" />.</b>
    ///     That instance is a process-wide singleton and doubles — as
    ///     <c>ICommunicationServiceClientAccessToken</c> — as the adapter's own service identity
    ///     towards the communication controller. Storing a user-bound token there would leak one
    ///     caller's identity into every concurrent request and into the adapter's own service calls.
    ///     <see cref="EnsureTokenAsync" /> keeps writing there because its token IS the service
    ///     identity; this one never may.
    /// </remarks>
    Task<string?> AcquireDelegatedTokenAsync(ITenantRepository tenantRepository, string wellKnownName,
        string subjectToken, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Answers <b>who the service account is</b>: the subject and the roles its own
    ///     client-credentials token carries (AB#5028). Third grant path next to
    ///     <see cref="EnsureTokenAsync" /> (which stores the token as the adapter's service identity)
    ///     and <see cref="AcquireDelegatedTokenAsync" /> (which returns a user-bound token).
    /// </summary>
    /// <param name="credentials">
    ///     The account's credentials — projected into the pipeline configuration by the controller
    ///     (AB#5027), so no repository read is needed to get here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The identity, or <c>null</c> when no token could be acquired.</returns>
    /// <remarks>
    ///     <b>Roles only exist on the token.</b> They are assigned to the client in the identity
    ///     service and are not part of the <c>ServiceAccountConfiguration</c> entity, so the only way
    ///     to learn them is to request a token and read its <c>role</c> claims. The token is parsed
    ///     locally without signature validation — see <see cref="JwtPayloadReader" /> for why that is
    ///     sound here — and is neither returned nor written into
    ///     <see cref="IServiceClientAccessToken" />: this call answers an identity question, it does
    ///     not hand out a credential.
    ///     <para>
    ///         Results are cached per <c>(TenantId, ClientId)</c> until shortly before the token's own
    ///         <c>exp</c>. The cache is deliberately NOT the <c>_tokenExpiresAt</c> field
    ///         <see cref="EnsureTokenAsync" /> uses: that one is not keyed by configuration and belongs
    ///         to the adapter's service identity, so sharing it would let one path suppress the other's
    ///         refresh.
    ///     </para>
    /// </remarks>
    Task<ServiceAccountIdentity?> AcquireServiceAccountIdentityAsync(ServiceAccountCredentials credentials,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of <see cref="IServiceAccountTokenService"/> that reads client credentials
/// from a ServiceAccountConfiguration runtime entity and acquires tokens via OAuth2 client credentials grant.
/// </summary>
internal class ServiceAccountTokenService : IServiceAccountTokenService
{
    /// <summary>
    ///     The OctoMesh delegation ("on-behalf-of") grant type. Deliberately an own URN and not the
    ///     RFC 8693 token-exchange one — the identity service binds one validator, and with it one
    ///     per-client opt-in, per grant type. Must stay byte-identical to
    ///     <c>DelegationConstants.OnBehalfOfGrantType</c> in octo-identity-services.
    /// </summary>
    internal const string OnBehalfOfGrantType = "urn:meshmakers:params:oauth:grant-type:on-behalf-of";

    /// <summary>
    ///     The OctoMesh impersonation grant type (AB#5114): the adapter's OWN confidential client
    ///     asks for a client-credentials-shaped token that runs as the service account named by
    ///     <see cref="RequestedClientIdParameterName" />, authorized by a MayActAs grant in the
    ///     identity service. Must stay byte-identical to the identity-side constant.
    /// </summary>
    internal const string ImpersonationGrantType = "urn:meshmakers:params:oauth:grant-type:impersonate";

    /// <summary>
    ///     Names the TARGET service-account client of an impersonation (and of an adapter-
    ///     authenticated on-behalf-of) request. Must stay byte-identical to the identity-side
    ///     constant.
    /// </summary>
    internal const string RequestedClientIdParameterName = "requested_client_id";

    /// <summary>
    ///     Deploy-time template token an older communication controller projects UNRESOLVED into a
    ///     configuration's <c>IssuerUri</c>. Semantically the same statement as an empty value —
    ///     "use the adapter's own installation" (AB#5115) — so it is treated exactly like one.
    ///     Compared case-insensitively.
    /// </summary>
    internal const string UnresolvedAuthorityTemplateToken = "{{service.authority}}";

    /// <summary>RFC 8693 token type identifier for an access token, sent as <c>subject_token_type</c>.</summary>
    internal const string AccessTokenTypeIdentifier = "urn:ietf:params:oauth:token-type:access_token";

    private static readonly HttpClient SharedTokenHttpClient = new();

    /// <summary>
    ///     Safety margin subtracted from a token's <c>exp</c> before it is treated as expired, so a
    ///     cached identity is never handed out for a token that dies in flight.
    /// </summary>
    private static readonly TimeSpan IdentityCacheSkew = TimeSpan.FromSeconds(60);

    private readonly HttpClient _tokenHttpClient;
    private readonly ILogger<ServiceAccountTokenService> _logger;
    private readonly IServiceClientAccessToken _serviceClientAccessToken;
    private readonly AdapterOptions _adapterOptions;
    private readonly MeshAdapterConfiguration _meshAdapterConfiguration;

    /// <summary>
    ///     Cached service-account identities, keyed by <c>(TenantId, ClientId)</c> — the pair that
    ///     decides which token would be issued. Process-wide because the answer is too: several
    ///     pipelines of the same adapter share one account, and the whole point of the cache is that a
    ///     high-frequency event trigger does not pay a token round trip per execution. The impersonated
    ///     path (AB#5114) shares this cache and keys by the TARGET service-account client id, not by
    ///     the adapter's own — the issued token runs as the target, so the target is the identity.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string TenantId, string ClientId),
        ServiceAccountIdentity> _identityCache = new();

    private DateTime _tokenExpiresAt = DateTime.MinValue;

    public ServiceAccountTokenService(IServiceClientAccessToken serviceClientAccessToken,
        ILogger<ServiceAccountTokenService> logger, IOptions<AdapterOptions> adapterOptions,
        IOptions<MeshAdapterConfiguration> meshAdapterConfiguration)
        : this(serviceClientAccessToken, logger, SharedTokenHttpClient, adapterOptions.Value,
            meshAdapterConfiguration.Value)
    {
    }

    /// <summary>Test seam: lets a unit test script the token endpoint without a server.</summary>
    internal ServiceAccountTokenService(IServiceClientAccessToken serviceClientAccessToken,
        ILogger<ServiceAccountTokenService> logger, HttpClient tokenHttpClient,
        AdapterOptions? adapterOptions = null, MeshAdapterConfiguration? meshAdapterConfiguration = null)
    {
        _serviceClientAccessToken = serviceClientAccessToken;
        _logger = logger;
        _tokenHttpClient = tokenHttpClient;
        _adapterOptions = adapterOptions ?? new AdapterOptions();
        _meshAdapterConfiguration = meshAdapterConfiguration ?? new MeshAdapterConfiguration();
    }

    /// <summary>
    ///     Whether <paramref name="clientSecret" /> can authenticate a token request. A blueprint
    ///     that provisions the configuration without a secret leaves the attribute empty, and an
    ///     older seed leaves an angle-bracket placeholder (<c>&lt;insert secret here&gt;</c>) behind —
    ///     both mean "no secret", not "this secret" (AB#5114).
    /// </summary>
    internal static bool IsSecretUsable(string? clientSecret)
    {
        return !string.IsNullOrWhiteSpace(clientSecret) && !clientSecret.TrimStart().StartsWith('<');
    }

    /// <summary>
    ///     Whether the adapter can authenticate AS ITSELF — the precondition of the impersonation
    ///     and adapter-authenticated delegation paths (AB#5114). Stricter than
    ///     <see cref="AdapterOptions.IsEnabled" /> on purpose: both grants authenticate with a client
    ///     secret, so a public adapter client cannot use them.
    /// </summary>
    private bool HasOwnClientCredentials =>
        !string.IsNullOrWhiteSpace(_adapterOptions.ClientId)
        && !string.IsNullOrWhiteSpace(_adapterOptions.ClientSecret);

    public async Task EnsureTokenAsync(ITenantRepository tenantRepository, string wellKnownName)
    {
        // Skip if token is still valid (with 60s buffer)
        if (!string.IsNullOrEmpty(_serviceClientAccessToken.AccessToken)
            && _tokenExpiresAt > DateTime.UtcNow.AddSeconds(60))
        {
            return;
        }

        var configuration = await ReadConfigurationAsync(tenantRepository, wellKnownName);
        if (configuration == null)
        {
            return;
        }

        var issuerUri = ResolveIssuerUri(configuration.IssuerUri, wellKnownName);
        if (issuerUri == null)
        {
            return;
        }

        var tenantId = ResolveTenantId(configuration.TenantId, tenantRepository.TenantId, wellKnownName);

        // Decide the path BEFORE discovery so a hopeless configuration fails without a network call.
        var mode = SelectAcquisitionMode(configuration, wellKnownName);
        if (mode == null)
        {
            return;
        }

        // Discover token endpoint
        var disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri));
        if (disco.IsError)
        {
            _logger.LogError("Failed to discover token endpoint at {IssuerUri}: {Error}",
                issuerUri, disco.Error);
            return;
        }

        var tokenRequest = CreateAmbientTokenRequest(disco.TokenEndpoint, configuration, tenantId, mode.Value);

        var response = await _tokenHttpClient.RequestTokenAsync(tokenRequest);

        if (response.IsError)
        {
            _logger.LogError("Failed to acquire token from {IssuerUri}: {Error}", issuerUri,
                response.Error);
            return;
        }

        _serviceClientAccessToken.AccessToken = response.AccessToken;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        _logger.LogInformation("Service account token acquired for client {ClientId} ({Mode}), expires at {ExpiresAt}",
            configuration.ClientId, mode.Value, _tokenExpiresAt);
    }

    /// <inheritdoc />
    public async Task<string?> AcquireDelegatedTokenAsync(ITenantRepository tenantRepository, string wellKnownName,
        string subjectToken, CancellationToken cancellationToken = default)
    {
        // Not cached, deliberately. A delegated token is bound to ONE user, so any cache would have
        // to be keyed by (subject, configuration) — and the only key material available here is the
        // subject token itself, which must not be held in a process-wide dictionary any longer than
        // the request that carried it. The existing _tokenExpiresAt field is not reused either: it
        // is not even keyed by configuration name and belongs to the service identity, so sharing it
        // would let a delegated acquisition suppress a service-token refresh (and vice versa).
        // The cost is one token round trip per AI query, against an LLM call that takes seconds.
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            _logger.LogError(
                "Delegation via ServiceAccountConfiguration '{WellKnownName}' requested without a caller token",
                wellKnownName);
            return null;
        }

        var configuration = await ReadConfigurationAsync(tenantRepository, wellKnownName);
        if (configuration == null)
        {
            return null;
        }

        var issuerUri = ResolveIssuerUri(configuration.IssuerUri, wellKnownName);
        if (issuerUri == null)
        {
            return null;
        }

        var tenantId = ResolveTenantId(configuration.TenantId, tenantRepository.TenantId, wellKnownName);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // The grant is same-tenant and the identity service requires acr_values=tenant:X to wire
            // the request to a tenant at all; without it the token request is rejected with a
            // message about a tenant that could not be resolved, which reads like an outage.
            // Since AB#5115 an empty TenantId on the configuration means "the adapter's tenant",
            // so this only fires when the adapter itself does not know its tenant either.
            _logger.LogError(
                "ServiceAccountConfiguration '{WellKnownName}' carries no TenantId and the adapter's own tenant is unknown; the delegation grant requires acr_values=tenant:{{tenantId}}",
                wellKnownName);
            return null;
        }

        var mode = SelectAcquisitionMode(configuration, wellKnownName);
        if (mode == null)
        {
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery throw
            // ("Malformed URL") rather than report an error — the AB#4541 shape. This method
            // promises null on failure, so it is caught here; the CALLER decides whether that is
            // fatal, and for the delegation path it is.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError("Failed to discover token endpoint at {IssuerUri} for delegation: {Error}",
                issuerUri, disco.Error);
            return null;
        }

        var tokenRequest = new TokenRequest
        {
            Address = disco.TokenEndpoint,
            GrantType = OnBehalfOfGrantType,
            Parameters =
            {
                { OidcConstants.TokenRequest.SubjectToken, subjectToken },
                { OidcConstants.TokenRequest.SubjectTokenType, AccessTokenTypeIdentifier },
                { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{tenantId}" },
                {
                    // DefaultScopes.None → exactly "octo_api", notably WITHOUT offline_access: the
                    // role intersection is computed at issuance, so a refresh token would freeze it
                    // and keep a revoked role alive. The identity service rejects the scope outright
                    // (invalid_scope) for that reason — asking for it would fail the whole request.
                    OidcConstants.TokenRequest.Scope,
                    CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                }
            }
        };

        if (mode == AcquisitionMode.ServiceAccountSecret)
        {
            // The service account authenticates with its own client credentials; the identity
            // service proves client_id before the delegation validator ever runs.
            tokenRequest.ClientId = configuration.ClientId;
            tokenRequest.ClientSecret = configuration.ClientSecret;
        }
        else
        {
            // AB#5114: the ADAPTER authenticates with its own client credentials and names the
            // service account it acts through; a MayActAs grant in the identity service authorizes
            // the pairing. subject_token / acr semantics are unchanged.
            tokenRequest.ClientId = _adapterOptions.ClientId!;
            tokenRequest.ClientSecret = _adapterOptions.ClientSecret;
            tokenRequest.Parameters.Add(RequestedClientIdParameterName, configuration.ClientId);
        }

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Returning null rather than throwing: the caller decides whether a missing delegated
            // token is fatal (it is, for the fail-closed MCP path) — but it must decide with a
            // message, so the cause is logged here where it is known.
            _logger.LogError(ex,
                "Delegated token request to {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (response.IsError)
        {
            _logger.LogError(
                "Delegation rejected by {IssuerUri} for ServiceAccountConfiguration '{WellKnownName}' in tenant '{TenantId}': {Error} ({ErrorDescription})",
                issuerUri, wellKnownName, tenantId, response.Error,
                response.ErrorDescription ?? "no description");
            return null;
        }

        if (string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Delegation via ServiceAccountConfiguration '{WellKnownName}' succeeded but returned no access token",
                wellKnownName);
            return null;
        }

        // 🔴 NOT written to _serviceClientAccessToken — see the interface remarks. The token is
        // never logged either; only the fact that one was issued.
        _logger.LogInformation(
            "Delegated token acquired via ServiceAccountConfiguration '{WellKnownName}' (client {ClientId}, {Mode}) in tenant '{TenantId}', expires in {ExpiresIn}s",
            wellKnownName, configuration.ClientId, mode.Value, tenantId, response.ExpiresIn);

        return response.AccessToken;
    }

    /// <summary>
    /// Reads the <c>System.Communication/ServiceAccountConfiguration</c> entity and validates that it
    /// carries the credentials any grant needs. Returns null (after logging) when it is missing or
    /// incomplete — shared by both grant paths so they fail identically on a broken configuration.
    /// Only <c>ClientId</c> is mandatory: an empty <c>IssuerUri</c> or <c>TenantId</c> means "the
    /// adapter's own installation / tenant" since AB#5115, and an empty <c>ClientSecret</c> selects
    /// the impersonation path since AB#5114.
    /// </summary>
    private async Task<ServiceAccountCredentials?> ReadConfigurationAsync(ITenantRepository tenantRepository,
        string wellKnownName)
    {
        // Deliberately the parameterless SYSTEM session (AB#5028): this read is what ANSWERS the
        // identity question, so scoping it to the identity it is about would be circular — and the
        // configuration is platform credential material, not tenant business data.
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();
        queryOptions.AddFieldFilter("rtWellKnownName", FieldFilterOperator.Equals, wellKnownName);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            new RtCkId<CkTypeId>("System.Communication/ServiceAccountConfiguration"),
            queryOptions, take: 1);

        var configEntity = result.Items.FirstOrDefault();
        if (configEntity == null)
        {
            _logger.LogWarning("ServiceAccountConfiguration '{WellKnownName}' not found, cannot acquire token",
                wellKnownName);
            return null;
        }

        var issuerUri = configEntity.GetAttributeValueOrDefault("IssuerUri") as string;
        var clientId = configEntity.GetAttributeValueOrDefault("ClientId") as string;
        var clientSecret = configEntity.GetAttributeValueOrDefault("ClientSecret") as string;
        var tenantId = configEntity.GetAttributeValueOrDefault("TenantId") as string;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning(
                "ServiceAccountConfiguration '{WellKnownName}' carries no ClientId, cannot acquire token",
                wellKnownName);
            return null;
        }

        return new ServiceAccountCredentials(issuerUri ?? string.Empty, clientId, clientSecret, tenantId);
    }

    /// <inheritdoc />
    public async Task<ServiceAccountIdentity?> AcquireServiceAccountIdentityAsync(
        ServiceAccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        var tenantId = ResolveTenantId(credentials.TenantId, adapterTenantId: null, credentials.ClientId);

        var cacheKey = (tenantId ?? string.Empty, credentials.ClientId);
        if (_identityCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached;
        }

        var issuerUri = ResolveIssuerUri(credentials.IssuerUri, credentials.ClientId);
        if (issuerUri == null)
        {
            return null;
        }

        var mode = SelectAcquisitionMode(credentials, credentials.ClientId);
        if (mode == null)
        {
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery THROW
            // ("Malformed URL") rather than report an error — the AB#4541 shape.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                issuerUri, credentials.ClientId);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError(
                "Failed to discover token endpoint at {IssuerUri} while resolving the identity of service account client {ClientId}: {Error}",
                issuerUri, credentials.ClientId, disco.Error);
            return null;
        }

        var tokenRequest = CreateAmbientTokenRequest(disco.TokenEndpoint, credentials, tenantId, mode.Value);

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Token request to {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                issuerUri, credentials.ClientId);
            return null;
        }

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Identity service {IssuerUri} refused a token for service account client {ClientId} in tenant '{TenantId}': {Error} ({ErrorDescription})",
                issuerUri, credentials.ClientId, tenantId,
                response.Error ?? "no token", response.ErrorDescription ?? "no description");
            return null;
        }

        if (!JwtPayloadReader.TryRead(response.AccessToken, out var claims))
        {
            // An opaque (reference) token carries no readable claims. Reporting no identity is the
            // only honest answer — inventing an empty-role one would look like a correctly resolved
            // account with no permissions, which fails silently everywhere downstream.
            _logger.LogError(
                "The token issued for service account client {ClientId} is not a readable JWT; its identity cannot be resolved",
                credentials.ClientId);
            return null;
        }

        // A client-credentials token has no 'sub'; 'client_id' is the subject the engine stamps and
        // filters on then — the same precedence RuntimeSecurityContextResolver uses in octo-mcp-service.
        // An impersonated token (AB#5114) is CC-shaped for the TARGET account, so the same read applies;
        // credentials.ClientId (the target) stays the last-resort fallback either way.
        var subjectId = claims.Subject ?? claims.ClientId ?? credentials.ClientId;

        // The token's own exp is the truth; expires_in is the fallback for an issuer that omits it.
        var expiresAt = (claims.ExpiresAtUtc ?? DateTime.UtcNow.AddSeconds(response.ExpiresIn)) - IdentityCacheSkew;

        var identity = new ServiceAccountIdentity(subjectId, claims.Roles, expiresAt);

        if (identity.Roles.Count == 0)
        {
            // Not an error — but an account with no roles produces an empty role set everywhere
            // downstream, and every role-gated read then returns nothing while every log stays quiet.
            // See the AB#5027 note in octo-communication-controller-services/CLAUDE.md.
            _logger.LogWarning(
                "Service account client {ClientId} in tenant '{TenantId}' carries no roles; pipelines running under it see only data that needs none",
                credentials.ClientId, tenantId);
        }
        else
        {
            _logger.LogInformation(
                "Resolved service account identity for client {ClientId} in tenant '{TenantId}': subject {SubjectId} with {RoleCount} role(s)",
                credentials.ClientId, tenantId, subjectId, identity.Roles.Count);
        }

        _identityCache[cacheKey] = identity;
        return identity;
    }

    /// <summary>How the token for a service account is obtained (AB#5114).</summary>
    internal enum AcquisitionMode
    {
        /// <summary>The account authenticates itself with the secret stored on the configuration.</summary>
        ServiceAccountSecret,

        /// <summary>
        ///     The adapter authenticates with its OWN client credentials and names the account via
        ///     <c>requested_client_id</c>; a MayActAs grant authorizes the pairing.
        /// </summary>
        Impersonation
    }

    /// <summary>
    ///     Decides between the two acquisition paths — the ONE decision every grant path shares
    ///     (AB#5114): a usable secret on the configuration keeps the pre-AB#5114 behaviour
    ///     byte-for-byte; without one the adapter's own credentials carry the request through the
    ///     impersonation / adapter-authenticated delegation grants. Returns null (after logging the
    ///     one actionable error) when neither is possible.
    /// </summary>
    private AcquisitionMode? SelectAcquisitionMode(ServiceAccountCredentials configuration, string configurationName)
    {
        if (IsSecretUsable(configuration.ClientSecret))
        {
            return AcquisitionMode.ServiceAccountSecret;
        }

        if (HasOwnClientCredentials)
        {
            _logger.LogDebug(
                "ServiceAccountConfiguration '{ConfigurationName}' carries no usable ClientSecret; impersonating client {TargetClientId} with the adapter's own identity '{OwnClientId}' (AB#5114)",
                configurationName, configuration.ClientId, _adapterOptions.ClientId);
            return AcquisitionMode.Impersonation;
        }

        _logger.LogError(
            "ServiceAccountConfiguration '{ConfigurationName}' (client {ClientId}) carries no usable ClientSecret and the adapter has no identity of its own (Adapter:ClientId / Adapter:ClientSecret are not configured). Either store a ClientSecret on the configuration, or give the adapter its own client credentials plus a MayActAs grant in the identity service so it can impersonate the service account (AB#5114)",
            configurationName, configuration.ClientId);
        return null;
    }

    /// <summary>
    ///     Builds the token request of the two AMBIENT paths (<see cref="EnsureTokenAsync" /> and
    ///     <see cref="AcquireServiceAccountIdentityAsync" />): plain client credentials when the
    ///     configuration carries a usable secret, the impersonation grant (AB#5114) when the
    ///     adapter's own identity carries the request. Both responses are CC-shaped tokens for the
    ///     service account, which is why the two callers can treat them identically.
    /// </summary>
    private TokenRequest CreateAmbientTokenRequest(string? tokenEndpoint, ServiceAccountCredentials configuration,
        string? tenantId, AcquisitionMode mode)
    {
        var tokenRequest = new TokenRequest
        {
            Address = tokenEndpoint,
            Parameters =
            {
                {
                    OidcConstants.TokenRequest.Scope,
                    CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                }
            }
        };

        if (mode == AcquisitionMode.ServiceAccountSecret)
        {
            tokenRequest.GrantType = OidcConstants.GrantTypes.ClientCredentials;
            tokenRequest.ClientId = configuration.ClientId;
            tokenRequest.ClientSecret = configuration.ClientSecret;
        }
        else
        {
            tokenRequest.GrantType = ImpersonationGrantType;
            tokenRequest.ClientId = _adapterOptions.ClientId!;
            tokenRequest.ClientSecret = _adapterOptions.ClientSecret;
            tokenRequest.Parameters.Add(RequestedClientIdParameterName, configuration.ClientId);
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tokenRequest.Parameters.Add(OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{tenantId}");
        }

        return tokenRequest;
    }

    /// <summary>
    ///     Resolves the issuer every token request goes to (AB#5115). An explicit URL on the
    ///     configuration wins — that is the deliberate foreign/pinned-installation case. An EMPTY
    ///     value (or the unresolved <c>{{service.authority}}</c> deploy-time token an older
    ///     controller leaves behind) is the DEFAULT, not damage: it means "this adapter's own
    ///     installation", answered by <c>Adapter:IssuerUri</c> (the adapter's own identity,
    ///     AB#5072) and finally <c>Adapter:AuthorityUrl</c> — which on this adapter is also the
    ///     authority incoming bearers are validated against, so the two chain entries of the design
    ///     collapse into one key here. Returns null (after logging every place that was empty) when
    ///     nothing is configured anywhere.
    /// </summary>
    private string? ResolveIssuerUri(string? configuredIssuerUri, string configurationName)
    {
        if (!string.IsNullOrWhiteSpace(configuredIssuerUri)
            && !string.Equals(configuredIssuerUri.Trim(), UnresolvedAuthorityTemplateToken,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Issuer for service account configuration '{ConfigurationName}' comes from the configuration itself: {IssuerUri}",
                configurationName, configuredIssuerUri);
            return configuredIssuerUri;
        }

        if (!string.IsNullOrWhiteSpace(_adapterOptions.IssuerUri))
        {
            _logger.LogDebug(
                "Issuer for service account configuration '{ConfigurationName}' comes from Adapter:IssuerUri: {IssuerUri}",
                configurationName, _adapterOptions.IssuerUri);
            return _adapterOptions.IssuerUri;
        }

        if (!string.IsNullOrWhiteSpace(_meshAdapterConfiguration.AuthorityUrl))
        {
            _logger.LogDebug(
                "Issuer for service account configuration '{ConfigurationName}' comes from Adapter:AuthorityUrl (the bearer-validation authority): {IssuerUri}",
                configurationName, _meshAdapterConfiguration.AuthorityUrl);
            return _meshAdapterConfiguration.AuthorityUrl;
        }

        _logger.LogError(
            "No issuer for service account configuration '{ConfigurationName}': its IssuerUri is empty (which means 'the adapter's own installation', AB#5115), but Adapter:IssuerUri (the adapter's own identity, AB#5072) and Adapter:AuthorityUrl (the bearer-validation authority) are empty too. Configure one of them",
            configurationName);
        return null;
    }

    /// <summary>
    ///     Resolves the tenant a token request acts in (AB#5115): the configuration's own
    ///     <c>TenantId</c> when set, otherwise the tenant the adapter is running for. Null only when
    ///     both are empty — the caller decides whether that is fatal (it is for the delegation
    ///     grant, which hard-requires <c>acr_values=tenant:X</c>).
    /// </summary>
    private string? ResolveTenantId(string? configuredTenantId, string? adapterTenantId, string configurationName)
    {
        if (!string.IsNullOrWhiteSpace(configuredTenantId))
        {
            return configuredTenantId;
        }

        var ownTenantId = !string.IsNullOrWhiteSpace(adapterTenantId) ? adapterTenantId : _adapterOptions.TenantId;
        if (!string.IsNullOrWhiteSpace(ownTenantId))
        {
            _logger.LogDebug(
                "Service account configuration '{ConfigurationName}' carries no TenantId; using the adapter's own tenant '{TenantId}' (AB#5115)",
                configurationName, ownTenantId);
            return ownTenantId;
        }

        return null;
    }

    /// <summary>
    ///     Discovery request that tolerates a split-horizon issuer: in hybrid installations the
    ///     adapter reaches the identity service under a cluster-visible host (e.g.
    ///     <c>https://mac.local:5003</c> from a local kind pod) while the identity service mints a
    ///     FIXED issuer under its canonical host (<c>https://localhost:5003/</c>). IdentityModel's
    ///     default policy rejects that as "Issuer name does not match authority" — but the
    ///     configured IssuerUri is trusted by definition here (we send the client secret to it),
    ///     TLS still authenticates the host, and the same mismatch is already accepted on the
    ///     validation side via AdditionalValidIssuers (AB#4922).
    ///     <para>
    ///         Endpoint-authority validation is off for the same reason: the identity service
    ///         legitimately emits MIXED endpoint hosts in one split-horizon document — most
    ///         endpoints derived from the request host (e.g. <c>https://mac.local:5003</c>) but
    ///         some, at least <c>check_session_iframe</c>, from the fixed canonical issuer
    ///         (<c>https://localhost:5003</c>). With the default policy IdentityModel rejects that
    ///         as "Endpoint is on a different host than authority". Since the document comes from
    ///         the trusted, TLS-authenticated IssuerUri anyway, endpoint-host consistency checks
    ///         add nothing here either.
    ///     </para>
    /// </summary>
    private static DiscoveryDocumentRequest CreateDiscoveryRequest(string issuerUri)
    {
        return new DiscoveryDocumentRequest
        {
            Address = issuerUri,
            Policy = { ValidateIssuerName = false, ValidateEndpoints = false }
        };
    }

}

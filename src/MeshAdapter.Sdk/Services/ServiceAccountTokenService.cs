using IdentityModel;
using IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    ///     Cached service-account identities, keyed by <c>(TenantId, ClientId)</c> — the pair that
    ///     decides which token would be issued. Process-wide because the answer is too: several
    ///     pipelines of the same adapter share one account, and the whole point of the cache is that a
    ///     high-frequency event trigger does not pay a token round trip per execution.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string TenantId, string ClientId),
        ServiceAccountIdentity> _identityCache = new();

    private DateTime _tokenExpiresAt = DateTime.MinValue;

    public ServiceAccountTokenService(IServiceClientAccessToken serviceClientAccessToken,
        ILogger<ServiceAccountTokenService> logger)
        : this(serviceClientAccessToken, logger, SharedTokenHttpClient)
    {
    }

    /// <summary>Test seam: lets a unit test script the token endpoint without a server.</summary>
    internal ServiceAccountTokenService(IServiceClientAccessToken serviceClientAccessToken,
        ILogger<ServiceAccountTokenService> logger, HttpClient tokenHttpClient)
    {
        _serviceClientAccessToken = serviceClientAccessToken;
        _logger = logger;
        _tokenHttpClient = tokenHttpClient;
    }

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

        // Discover token endpoint
        var disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(configuration.IssuerUri);
        if (disco.IsError)
        {
            _logger.LogError("Failed to discover token endpoint at {IssuerUri}: {Error}",
                configuration.IssuerUri, disco.Error);
            return;
        }

        // Request client credentials token
        var tokenRequest = new ClientCredentialsTokenRequest
        {
            Address = disco.TokenEndpoint,
            ClientId = configuration.ClientId,
            ClientSecret = configuration.ClientSecret,
            Scope = CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
        };

        if (!string.IsNullOrWhiteSpace(configuration.TenantId))
        {
            tokenRequest.Parameters.Add("acr_values", $"tenant:{configuration.TenantId}");
        }

        var response = await _tokenHttpClient.RequestClientCredentialsTokenAsync(tokenRequest);

        if (response.IsError)
        {
            _logger.LogError("Failed to acquire token from {IssuerUri}: {Error}", configuration.IssuerUri,
                response.Error);
            return;
        }

        _serviceClientAccessToken.AccessToken = response.AccessToken;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        _logger.LogInformation("Service account token acquired for client {ClientId}, expires at {ExpiresAt}",
            configuration.ClientId, _tokenExpiresAt);
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

        if (string.IsNullOrWhiteSpace(configuration.TenantId))
        {
            // The grant is same-tenant and the identity service requires acr_values=tenant:X to wire
            // the request to a tenant at all; without it the token request is rejected with a
            // message about a tenant that could not be resolved, which reads like an outage.
            _logger.LogError(
                "ServiceAccountConfiguration '{WellKnownName}' carries no TenantId; the delegation grant requires acr_values=tenant:{{tenantId}}",
                wellKnownName);
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(configuration.IssuerUri, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery throw
            // ("Malformed URL") rather than report an error — the AB#4541 shape. This method
            // promises null on failure, so it is caught here; the CALLER decides whether that is
            // fatal, and for the delegation path it is.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                configuration.IssuerUri, wellKnownName);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError("Failed to discover token endpoint at {IssuerUri} for delegation: {Error}",
                configuration.IssuerUri, disco.Error);
            return null;
        }

        var tokenRequest = new TokenRequest
        {
            Address = disco.TokenEndpoint,
            GrantType = OnBehalfOfGrantType,
            // The service account authenticates with its own client credentials; the identity
            // service proves client_id before the delegation validator ever runs.
            ClientId = configuration.ClientId,
            ClientSecret = configuration.ClientSecret,
            Parameters =
            {
                { OidcConstants.TokenRequest.SubjectToken, subjectToken },
                { OidcConstants.TokenRequest.SubjectTokenType, AccessTokenTypeIdentifier },
                { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{configuration.TenantId}" },
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
                configuration.IssuerUri, wellKnownName);
            return null;
        }

        if (response.IsError)
        {
            _logger.LogError(
                "Delegation rejected by {IssuerUri} for ServiceAccountConfiguration '{WellKnownName}' in tenant '{TenantId}': {Error} ({ErrorDescription})",
                configuration.IssuerUri, wellKnownName, configuration.TenantId, response.Error,
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
            "Delegated token acquired via ServiceAccountConfiguration '{WellKnownName}' (client {ClientId}) in tenant '{TenantId}', expires in {ExpiresIn}s",
            wellKnownName, configuration.ClientId, configuration.TenantId, response.ExpiresIn);

        return response.AccessToken;
    }

    /// <summary>
    /// Reads the <c>System.Communication/ServiceAccountConfiguration</c> entity and validates that it
    /// carries the credentials any grant needs. Returns null (after logging) when it is missing or
    /// incomplete — shared by both grant paths so they fail identically on a broken configuration.
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

        if (string.IsNullOrWhiteSpace(issuerUri) || string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning(
                "ServiceAccountConfiguration '{WellKnownName}' has incomplete credentials (IssuerUri or ClientId missing)",
                wellKnownName);
            return null;
        }

        return new ServiceAccountCredentials(issuerUri, clientId, clientSecret, tenantId);
    }

    /// <inheritdoc />
    public async Task<ServiceAccountIdentity?> AcquireServiceAccountIdentityAsync(
        ServiceAccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        var cacheKey = (credentials.TenantId ?? string.Empty, credentials.ClientId);
        if (_identityCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(credentials.IssuerUri, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery THROW
            // ("Malformed URL") rather than report an error — the AB#4541 shape.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                credentials.IssuerUri, credentials.ClientId);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError(
                "Failed to discover token endpoint at {IssuerUri} while resolving the identity of service account client {ClientId}: {Error}",
                credentials.IssuerUri, credentials.ClientId, disco.Error);
            return null;
        }

        var tokenRequest = new ClientCredentialsTokenRequest
        {
            Address = disco.TokenEndpoint,
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            Scope = CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
        };

        if (!string.IsNullOrWhiteSpace(credentials.TenantId))
        {
            tokenRequest.Parameters.Add("acr_values", $"tenant:{credentials.TenantId}");
        }

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestClientCredentialsTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Token request to {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                credentials.IssuerUri, credentials.ClientId);
            return null;
        }

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Identity service {IssuerUri} refused a token for service account client {ClientId} in tenant '{TenantId}': {Error} ({ErrorDescription})",
                credentials.IssuerUri, credentials.ClientId, credentials.TenantId,
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
                credentials.ClientId, credentials.TenantId);
        }
        else
        {
            _logger.LogInformation(
                "Resolved service account identity for client {ClientId} in tenant '{TenantId}': subject {SubjectId} with {RoleCount} role(s)",
                credentials.ClientId, credentials.TenantId, subjectId, identity.Roles.Count);
        }

        _identityCache[cacheKey] = identity;
        return identity;
    }
}

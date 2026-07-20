using System.Collections.Concurrent;
using IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Acquires and manages OAuth2 access tokens from ServiceAccountConfiguration entities
/// (client credentials grant). This is the single token service for every consumer in
/// the adapter: DeployPipeline@1 (publishes the token to the shared service-client
/// credential), and the MCP-authenticating nodes LlmQuery@1 / McpToolCall@1 /
/// AnthropicAiQuery@1 (side-effect-free per-call tokens).
/// </summary>
public interface IServiceAccountTokenService
{
    /// <summary>
    /// Acquires a valid access token for the named ServiceAccountConfiguration and
    /// <b>publishes it to the adapter-global <c>IServiceClientAccessToken</c></b> so the
    /// shared service clients (e.g. the CommunicationServicesClient used by
    /// DeployPipeline@1) authenticate with it. Use this only when that global side
    /// effect is the point; use <see cref="GetAccessTokenAsync"/> everywhere else.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="tenantId">Tenant the configuration belongs to (cache isolation key)</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    Task EnsureTokenAsync(ITenantRepository tenantRepository, string tenantId, string wellKnownName);

    /// <summary>
    /// Acquires a valid access token for the named ServiceAccountConfiguration and
    /// returns it <b>without touching the adapter-global credential</b>. Tokens are
    /// cached per configuration name (60 s expiry buffer, single-flight acquisition),
    /// so two nodes referencing different service accounts never receive each other's
    /// token. Returns <c>null</c> when the configuration cannot be resolved or the
    /// grant fails — callers decide their own fallback (a warning is logged).
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="tenantId">Tenant the configuration belongs to. Part of the cache key —
    /// two tenants with a same-named ServiceAccountConfiguration must never share a token.</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    /// <param name="cancellationToken">Cancellation token (bounded by the caller's timeout budget)</param>
    Task<string?> GetAccessTokenAsync(ITenantRepository tenantRepository, string tenantId, string wellKnownName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of <see cref="IServiceAccountTokenService"/>: OIDC discovery +
/// client-credentials grant per ServiceAccountConfiguration, keyed in-memory cache.
/// Registered as a singleton — a typical hourly token is minted once per hour per
/// service account, not per pipeline message.
/// <para>
/// History note: before the AB#4315 consolidation this service cached a single token
/// identity-blind and always overwrote <c>IServiceClientAccessToken</c> — with two
/// different service accounts on one adapter, the second caller silently received the
/// first account's token, and MCP token acquisitions clobbered the adapter's own
/// service identity. The keyed cache + side-effect-free <c>GetAccessTokenAsync</c>
/// fix both; <c>EnsureTokenAsync</c> retains the publish-to-global behavior for the
/// deploy path where it is intentional.
/// </para>
/// </summary>
internal class ServiceAccountTokenService(
    IServiceClientAccessToken serviceClientAccessToken,
    ILogger<ServiceAccountTokenService> logger) : IServiceAccountTokenService
{
    private static readonly HttpClient TokenHttpClient = new();
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CachedToken> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _acquireLock = new(1, 1);

    internal sealed record CachedToken(string AccessToken, DateTime ExpiresAtUtc)
    {
        public bool IsValid => DateTime.UtcNow < ExpiresAtUtc - ExpiryBuffer;

        /// <summary>Redacts the token — record auto-ToString would print it into any log/exception.</summary>
        public override string ToString() => $"CachedToken {{ AccessToken = <redacted>, ExpiresAtUtc = {ExpiresAtUtc:O} }}";
    }

    public async Task EnsureTokenAsync(ITenantRepository tenantRepository, string tenantId, string wellKnownName)
    {
        var token = await GetAccessTokenAsync(tenantRepository, tenantId, wellKnownName);
        if (!string.IsNullOrEmpty(token))
        {
            // Deliberate global side effect: the shared service clients (Communication
            // ServicesClient et al.) read their credential from this instance.
            serviceClientAccessToken.AccessToken = token;
        }
    }

    public async Task<string?> GetAccessTokenAsync(ITenantRepository tenantRepository,
        string tenantId, string wellKnownName, CancellationToken cancellationToken = default)
    {
        // Tenant-scoped cache key: the adapter is single-tenant per process today, but
        // nothing in this API enforces that — a same-named configuration in another
        // tenant must never resolve to this tenant's token.
        var cacheKey = $"{tenantId}::{wellKnownName}";

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.IsValid)
        {
            return cached.AccessToken;
        }

        // Single-flight: concurrent pipeline messages must not stampede the identity
        // server when a token expires. Double-check after the wait.
        await _acquireLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached) && cached.IsValid)
            {
                return cached.AccessToken;
            }

            var acquired = await AcquireTokenAsync(tenantRepository, wellKnownName, cancellationToken);
            if (acquired is null)
            {
                return null;
            }

            _cache[cacheKey] = acquired;
            return acquired.AccessToken;
        }
        finally
        {
            _acquireLock.Release();
        }
    }

    /// <summary>
    /// Resolves the ServiceAccountConfiguration entity and performs the
    /// client-credentials grant. Virtual so unit tests can substitute the network
    /// round-trip while exercising the cache-keying and side-effect contracts.
    /// </summary>
    protected virtual async Task<CachedToken?> AcquireTokenAsync(ITenantRepository tenantRepository,
        string wellKnownName, CancellationToken cancellationToken)
    {
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
            logger.LogWarning("ServiceAccountConfiguration '{WellKnownName}' not found, cannot acquire token",
                wellKnownName);
            return null;
        }

        var issuerUri = configEntity.GetAttributeValueOrDefault("IssuerUri") as string;
        var clientId = configEntity.GetAttributeValueOrDefault("ClientId") as string;
        var clientSecret = configEntity.GetAttributeValueOrDefault("ClientSecret") as string;
        var tenantId = configEntity.GetAttributeValueOrDefault("TenantId") as string;

        if (string.IsNullOrWhiteSpace(issuerUri) || string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning(
                "ServiceAccountConfiguration '{WellKnownName}' has incomplete credentials (IssuerUri or ClientId missing)",
                wellKnownName);
            return null;
        }

        // Discover token endpoint
        var disco = await TokenHttpClient.GetDiscoveryDocumentAsync(issuerUri, cancellationToken);
        if (disco.IsError)
        {
            logger.LogError("Failed to discover token endpoint at {IssuerUri}: {Error}", issuerUri, disco.Error);
            return null;
        }

        // Request client credentials token
        var tokenRequest = new ClientCredentialsTokenRequest
        {
            Address = disco.TokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scope = CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tokenRequest.Parameters.Add("acr_values", $"tenant:{tenantId}");
        }

        var response = await TokenHttpClient.RequestClientCredentialsTokenAsync(tokenRequest, cancellationToken);

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken))
        {
            logger.LogError("Failed to acquire token from {IssuerUri}: {Error}", issuerUri, response.Error);
            return null;
        }

        var expiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        logger.LogInformation(
            "Service account token acquired for '{WellKnownName}' (client {ClientId}), expires at {ExpiresAt}",
            wellKnownName, clientId, expiresAt);

        return new CachedToken(response.AccessToken, expiresAt);
    }
}

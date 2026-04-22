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
}

/// <summary>
/// Implementation of <see cref="IServiceAccountTokenService"/> that reads client credentials
/// from a ServiceAccountConfiguration runtime entity and acquires tokens via OAuth2 client credentials grant.
/// </summary>
internal class ServiceAccountTokenService(
    IServiceClientAccessToken serviceClientAccessToken,
    ILogger<ServiceAccountTokenService> logger) : IServiceAccountTokenService
{
    private DateTime _tokenExpiresAt = DateTime.MinValue;
    private static readonly HttpClient TokenHttpClient = new();

    public async Task EnsureTokenAsync(ITenantRepository tenantRepository, string wellKnownName)
    {
        // Skip if token is still valid (with 60s buffer)
        if (!string.IsNullOrEmpty(serviceClientAccessToken.AccessToken)
            && _tokenExpiresAt > DateTime.UtcNow.AddSeconds(60))
        {
            return;
        }

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
            return;
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
            return;
        }

        // Discover token endpoint
        var disco = await TokenHttpClient.GetDiscoveryDocumentAsync(issuerUri);
        if (disco.IsError)
        {
            logger.LogError("Failed to discover token endpoint at {IssuerUri}: {Error}", issuerUri, disco.Error);
            return;
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

        var response = await TokenHttpClient.RequestClientCredentialsTokenAsync(tokenRequest);

        if (response.IsError)
        {
            logger.LogError("Failed to acquire token from {IssuerUri}: {Error}", issuerUri, response.Error);
            return;
        }

        serviceClientAccessToken.AccessToken = response.AccessToken;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        logger.LogInformation("Service account token acquired for client {ClientId}, expires at {ExpiresAt}",
            clientId, _tokenExpiresAt);
    }
}

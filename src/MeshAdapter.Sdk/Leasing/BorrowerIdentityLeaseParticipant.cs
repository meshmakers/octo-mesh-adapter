using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Makes the member act as the borrower for the duration of the lease, and stop being it
///     afterwards (AB#4924, concept §8 Q6).
/// </summary>
/// <remarks>
///     <para>
///         The lease carries the borrower's own <c>PipelineServiceAccount</c> credential (AB#5027).
///         This participant exchanges it for an access token and writes that token into the
///         process-wide <see cref="IServiceClientAccessToken" /> — the holder the SDK's SignalR client
///         and every service client read. From that moment the member <b>is</b> the borrower's adapter,
///         as far as every other OctoMesh service is concerned, which is exactly what Q6 decided and
///         why no new standing grant exists anywhere.
///     </para>
///     <para>
///         🔴 <b><c>acr_values=tenant:{borrower}</c> is not optional, and the result is verified.</b>
///         Since AB#5077 a token request without it is issued for the <b>system</b> tenant. A member
///         that acted on such a token would be a 403 if it were lucky and a cross-tenant read if it
///         were not — so the token's own <c>tenant_id</c> claim is checked against the lease before it
///         is published, and a mismatch <b>fails the lease</b>. This is the production half of the
///         identity assertion the increment's entry criteria demand; the test merely observes it.
///     </para>
///     <para>
///         🔴 <b>The credential is never persisted.</b> It lives on the <see cref="LeaseDto" />
///         parameter for the length of this call and nowhere else — not in a field, not in
///         configuration, not in a log. On leave the token holder is emptied, so the member presents no
///         credential at all between leases, which is the same state a freshly started unconfigured
///         adapter is in.
///     </para>
/// </remarks>
internal sealed class BorrowerIdentityLeaseParticipant : IAdapterLeaseParticipant
{
    private readonly AdapterOptions _adapterOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BorrowerIdentityLeaseParticipant> _logger;
    private readonly MeshAdapterConfiguration _meshAdapterConfiguration;
    private readonly IServiceClientAccessToken _serviceClientAccessToken;

    public BorrowerIdentityLeaseParticipant(IServiceClientAccessToken serviceClientAccessToken,
        IHttpClientFactory httpClientFactory, IOptions<AdapterOptions> adapterOptions,
        IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
        ILogger<BorrowerIdentityLeaseParticipant> logger)
        : this(serviceClientAccessToken, httpClientFactory.CreateClient(), adapterOptions.Value,
            meshAdapterConfiguration.Value, logger)
    {
    }

    /// <summary>Test seam: lets a test script the token endpoint without an identity service.</summary>
    internal BorrowerIdentityLeaseParticipant(IServiceClientAccessToken serviceClientAccessToken,
        HttpClient httpClient, AdapterOptions adapterOptions,
        MeshAdapterConfiguration meshAdapterConfiguration,
        ILogger<BorrowerIdentityLeaseParticipant> logger)
    {
        _serviceClientAccessToken = serviceClientAccessToken;
        _httpClient = httpClient;
        _adapterOptions = adapterOptions;
        _meshAdapterConfiguration = meshAdapterConfiguration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnterLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        var issuerUri = _adapterOptions.IssuerUri;
        if (string.IsNullOrWhiteSpace(issuerUri))
        {
            issuerUri = _meshAdapterConfiguration.AuthorityUrl;
        }

        if (string.IsNullOrWhiteSpace(issuerUri))
        {
            throw new InvalidOperationException(
                "A pool member cannot act as a borrower without an identity service: neither "
                + "Adapter:IssuerUri nor Adapter:AuthorityUrl is configured.");
        }

        var discovery = await _httpClient.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest
        {
            Address = issuerUri,
            Policy = { ValidateIssuerName = false, ValidateEndpoints = false }
        }, cancellationToken);

        if (discovery.IsError)
        {
            throw new InvalidOperationException(
                $"OIDC discovery at {issuerUri} failed while taking a lease for tenant "
                + $"'{lease.TenantId}': {discovery.Error}");
        }

        var tokenRequest = new ClientCredentialsTokenRequest
        {
            Address = discovery.TokenEndpoint,
            ClientId = lease.ClientId,
            ClientSecret = lease.ClientSecret,
            Scope = CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None),
            Parameters =
            {
                // 🔴 See the remarks on the type. Without this the identity service issues for the
                // system tenant (AB#5077).
                { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{lease.TenantId}" }
            }
        };

        var response = await _httpClient.RequestClientCredentialsTokenAsync(tokenRequest, cancellationToken);
        if (response.IsError)
        {
            // Names the client, never the secret. Same rule as every other credential path here.
            throw new InvalidOperationException(
                $"The identity service refused the borrower credential of client '{lease.ClientId}' for tenant "
                + $"'{lease.TenantId}': {response.Error}");
        }

        if (string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new InvalidOperationException(
                $"The identity service accepted the borrower credential of client '{lease.ClientId}' for tenant "
                + $"'{lease.TenantId}' but returned no access token.");
        }

        EnsureTokenBelongsToTheBorrower(lease, response.AccessToken);

        _serviceClientAccessToken.AccessToken = response.AccessToken;
        _logger.LogInformation(
            "This pool member now acts as client '{ClientId}' of leased tenant '{TenantId}'",
            lease.ClientId, lease.TenantId);
    }

    /// <inheritdoc />
    public Task LeaveLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        // Empty, not "the member's own token": a pool member has no identity of its own towards a
        // tenant, and leaving a previous borrower's token in place is the exact shape of a
        // cross-tenant call.
        _serviceClientAccessToken.AccessToken = null;
        _logger.LogDebug("Borrower identity dropped after releasing tenant '{TenantId}'", lease.TenantId);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Refuses a token that was not issued for the borrowing tenant.
    /// </summary>
    /// <remarks>
    ///     🔴 The failure this catches is silent by construction: a request that lost its
    ///     <c>acr_values</c> still returns <c>200</c> with a perfectly valid token — for the system
    ///     tenant. Asserting that a token exists proves nothing; asserting whose it is does.
    /// </remarks>
    private static void EnsureTokenBelongsToTheBorrower(LeaseDto lease, string accessToken)
    {
        if (!JwtPayloadReader.TryRead(accessToken, out var claims))
        {
            throw new InvalidOperationException(
                $"The token issued for the borrower of tenant '{lease.TenantId}' could not be parsed, so the "
                + "tenant it belongs to cannot be verified. The lease is refused.");
        }

        if (string.IsNullOrEmpty(claims.TenantId))
        {
            throw new InvalidOperationException(
                $"The token issued for client '{lease.ClientId}' carries no tenant_id claim, which since "
                + $"AB#5077 means it was issued for the SYSTEM tenant rather than for '{lease.TenantId}'. "
                + "The lease is refused.");
        }

        if (!string.Equals(claims.TenantId, lease.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The token issued for client '{lease.ClientId}' belongs to tenant '{claims.TenantId}', not to "
                + $"the leased tenant '{lease.TenantId}'. The lease is refused.");
        }
    }
}

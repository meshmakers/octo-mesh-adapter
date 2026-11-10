using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Creates a communication-controller client that authenticates with ONE given access token — a service
///     account's own token (AB#8699).
/// </summary>
/// <remarks>
///     The registered <see cref="ICommunicationServicesClient" /> singleton reads the process-wide
///     <c>IServiceClientAccessToken</c>, which is the adapter's own identity and is shared by every pipeline of
///     the process. A node that acts as a service account (<c>DeployPipeline@1</c>) must not write the account's
///     token into that holder: a concurrent pipeline would then call as that account, and the adapter's own
///     token refresh would replace the account's token under the node's feet. This factory gives such a node
///     a client of its own, with its own token holder; endpoint and tenant route are the same as the
///     singleton's (<see cref="CommunicationServiceClientOptions" />).
/// </remarks>
internal interface IServiceAccountCommunicationClientFactory
{
    /// <summary>Creates a client that sends <paramref name="accessToken" /> as its bearer token.</summary>
    /// <param name="accessToken">The token to authenticate with. Credential material — never log it.</param>
    ICommunicationServicesClient Create(string accessToken);
}

/// <inheritdoc />
internal sealed class ServiceAccountCommunicationClientFactory(IOptions<CommunicationServiceClientOptions> options)
    : IServiceAccountCommunicationClientFactory
{
    /// <inheritdoc />
    public ICommunicationServicesClient Create(string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return new CommunicationServicesClient(options.Value, new ServiceClientAccessToken { AccessToken = accessToken });
    }
}

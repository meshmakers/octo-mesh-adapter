using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// <see cref="StreamDataFixture" /> (MongoDB + a real CrateDB with stream data enabled) plus the real pipeline stack:
/// YAML through the <c>EtlDataOrchestrator</c> and the registered mesh adapter nodes. A pipeline that writes to an
/// archive therefore reaches the real <c>CrateDbStreamDataRepository</c>, not a fake (AB#8666).
/// </summary>
public class StreamDataPipelineFixture : StreamDataFixture
{
    public StreamDataPipelineFixture()
    {
        Services.AddSingleton(A.Fake<IPipelineLogger>());
        Services.AddOctoMeshAdapter();
        // AddOctoMeshAdapter wires the distributed tenant notifications; there is no broker in this test.
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());
        // AddOctoMeshAdapter binds options from IConfiguration, which the stream data stack then resolves when the
        // tenant enables stream data; this host has no configuration sources, so an empty one stands in.
        Services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    }
}

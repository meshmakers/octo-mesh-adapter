using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     <see cref="TenantSubtreeFixture" /> (the real tenant tree in one MongoDB) plus the pipeline services of
///     <see cref="PipelineExecutionFixture" />, so a test runs real pipeline YAML through the real orchestrator and the
///     registered mesh adapter nodes against the tree (AB#9768 reads, AB#9769 writes).
/// </summary>
public sealed class TenantSubtreePipelineFixture : TenantSubtreeFixture
{
    protected override async Task InitializeServicesAsync()
    {
        Services.AddSingleton(A.Fake<IPipelineLogger>());
        Services.AddOctoMeshAdapter();
        // AddOctoMeshAdapter wires the distributed tenant notifications; there is no broker in this test.
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());
        // AddOctoMeshAdapter binds options from IConfiguration, which the tenant context resolves when it opens a
        // tenant that already exists (stream-data check); this host has no configuration sources, so an empty one
        // stands in (as in StreamDataPipelineFixture).
        Services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        await base.InitializeServicesAsync();
    }
}

using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     <see cref="TenantSubtreeFixture" /> (the real tenant tree in one MongoDB) plus the pipeline services of
///     <see cref="PipelineExecutionFixture" />, so a test runs real pipeline YAML through the real orchestrator and the
///     registered mesh adapter nodes against the tree (AB#9768).
/// </summary>
public sealed class TenantSubtreePipelineFixture : TenantSubtreeFixture
{
    protected override async Task InitializeServicesAsync()
    {
        Services.AddSingleton(A.Fake<IPipelineLogger>());
        Services.AddOctoMeshAdapter();
        // AddOctoMeshAdapter wires the distributed tenant notifications; there is no broker in this test.
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());

        await base.InitializeServicesAsync();
    }
}

using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that runs real pipelines (YAML through the real <c>EtlDataOrchestrator</c> and the registered mesh
/// adapter nodes) against a MongoDB test container with the MeshAdapterIntegrationTest CK model imported.
/// </summary>
public class PipelineExecutionFixture : ImportCkModelFixture
{
    /// <summary>Pipeline logger of all executions; tests inspect the recorded warnings.</summary>
    public IPipelineLogger PipelineLogger { get; } = A.Fake<IPipelineLogger>();

    protected override async Task InitializeServicesAsync()
    {
        Services.AddSingleton(PipelineLogger);
        Services.AddCkModelMeshAdapterIntegrationTestV1();
        Services.AddOctoMeshAdapter();
        // AddOctoMeshAdapter wires the distributed tenant notifications; there is no broker in this test.
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());

        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"), operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                "Failed to import MeshAdapterIntegrationTest CK model: " +
                string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
        }
    }
}

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     Resolver stub with a mutable policy table (as in octo-construction-kit-engine-mongodb): a test sets
///     <see cref="Table" /> to protect types and resets it to <see cref="RtDataPolicyTable.Empty" /> afterwards.
/// </summary>
public sealed class TestDataPermissionResolver : IDataPermissionResolver
{
    public RtDataPolicyTable Table { get; set; } = RtDataPolicyTable.Empty;

    public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) => Task.FromResult(Table);

    public void Invalidate(string tenantId)
    {
    }
}

/// <summary>
///     <see cref="PipelineExecutionFixture" /> with a swappable data-permission resolver, so a pipeline run with a
///     restricted caller can be checked against protected types end to end (AB#6933).
/// </summary>
public class ShelvedFixture : PipelineExecutionFixture
{
    public ShelvedFixture()
    {
        // Last registration wins over the TryAddSingleton in AddRuntimeEngine.
        Services.AddSingleton<IDataPermissionResolver>(Resolver);
    }

    public TestDataPermissionResolver Resolver { get; } = new();

    /// <summary>
    ///     Drops all tenant data (including the imported CK models) and imports the test model again, so every test
    ///     starts from an empty tenant with the model in place.
    /// </summary>
    public async Task ResetAsync()
    {
        EnsureInitialized();
        Resolver.Table = RtDataPolicyTable.Empty;
        await ClearCollectionAsync();
        var operationResult = new OperationResult();
        await GetSystemContext().ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"), operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException("Failed to import MeshAdapterIntegrationTest CK model: " +
                                                string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
        }
    }
}

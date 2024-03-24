using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
public class ApplyChangesNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }
}

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[Node("ApplyChanges", 1, typeof(ApplyChangesNodeConfiguration))]
public class ApplyChangesNode(NodeDelegate next, IRetrieverEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<ApplyChangesNodeConfiguration>();

        var list = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>(c.TargetPropertyName, RtNewtonsoftSerializer.DefaultSerializer);

        if (list != null && list.Any())
        {
            OperationResult operationResult = new();
            await etlContext.TenantRepository.ApplyChangesAsync(etlContext.Session, list, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Error updating RtEntity");
                return;
            }
        }

        await next(dataContext);
    }
}
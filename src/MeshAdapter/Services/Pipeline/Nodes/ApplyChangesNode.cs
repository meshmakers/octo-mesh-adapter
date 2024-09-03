using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[NodeConfiguration(typeof(ApplyChangesNodeConfiguration))]
public class ApplyChangesNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<ApplyChangesNodeConfiguration>();

        var list = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>(c.TargetPropertyName, RtNewtonsoftSerializer.DefaultSerializer);

        if (list != null && list.Any())
        {
            // first we reverse the list because we are interested in the last update for each entity.
            list.Reverse();
            
            // then we are throwing away duplicates because we only want to update each entity once.
            list = list.DistinctBy(x => x.RtEntityId).ToList();

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
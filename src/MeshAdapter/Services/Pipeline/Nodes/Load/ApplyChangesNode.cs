using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[NodeConfiguration(typeof(ApplyChangesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ApplyChangesNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<ApplyChangesNodeConfiguration>();

        var list = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (list != null && list.Any())
        {
            // We use all inserts
            var resultUpdateInfos = list.Where(x => x.ModOption == EntityModOptions.Insert).ToList();

            // first we reverse the list because we are interested in the last update for each entity.
            var tempList = list.Where(x => x.ModOption != EntityModOptions.Insert).Reverse();

            // then we are throwing away duplicates because we only want to update each entity once.
            resultUpdateInfos.AddRange(tempList.DistinctBy(x => x.GetRtEntityId()));

            OperationResult operationResult = new();
            await etlContext.TenantRepository.ApplyChangesAsync(etlContext.Session, resultUpdateInfos, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                dataContext.NodeContext.Error("Error updating RtEntity");
                return;
            }
        }

        await next(dataContext);
    }
}
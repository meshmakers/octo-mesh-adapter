using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByTypeNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
       var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        var queryOptions = RtEntityQueryOptions.Create();
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);
        c.SortOrders.GetSortOrders(queryOptions);

        // AB#5028 — scoped: an ordinary read of tenant business data.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, queryOptions, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.Set(c.TargetPath, r, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        
        
        await next(dataContext, nodeContext);
    }
}
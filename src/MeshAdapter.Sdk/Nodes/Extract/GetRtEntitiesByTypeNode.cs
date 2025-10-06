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

        var ckTypeId = CkTypeIdHelper.ResolveCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        var dataQueryOperation = DataQueryOperation.Create();
        c.FieldFilters.GetFieldFilter(dataContext, dataQueryOperation);
        c.SortOrders.GetSortOrders(dataContext, dataQueryOperation);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, dataQueryOperation, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, r);
        
        
        await next(dataContext, nodeContext);
    }
}
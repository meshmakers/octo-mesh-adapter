using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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

        if (c.CkTypeId == null && c.CkTypeIdPath == null)
        {
            nodeContext.Error("CkTypeId is not set");
            return;
        }
        
        var ckTypeId = GetCkTypeId(c, dataContext);
        
        if (ckTypeId == null)
        {
            nodeContext.Error("No CkTypeId found");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        c.FieldFilters.GetFieldFilter(dataContext, dataQueryOperation);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, dataQueryOperation, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, r);
        
        
        await next(dataContext, nodeContext);
    }
    
    private static CkId<CkTypeId>? GetCkTypeId(GetRtEntitiesByTypeNodeConfiguration c, IDataContext dataContext)
    {
        if (c.CkTypeId != null)
        {
            return c.CkTypeId;
        }
        
        if (c.CkTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(c.CkTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw new InvalidOperationException($"No CkTypeId found at path '{c.CkTypeIdPath}'");
            }
            return new CkId<CkTypeId>(ckTypeIdValue);
        }
        
        return null;
    }
}
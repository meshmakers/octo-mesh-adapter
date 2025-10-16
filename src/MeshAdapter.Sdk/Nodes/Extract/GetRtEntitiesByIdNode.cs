using GraphQL.Client.Abstractions.Utilities;
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
[NodeConfiguration(typeof(GetRtEntitiesByIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByIdNode(NodeDelegate next, IMeshEtlContext context) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var etlContext = context;

        var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        if (c.RtIds == null && c.RtIdsPath == null)
        {
            nodeContext.Error("RtIds is not set");
            return;
        }
        
        var rtIds = GetRtIds(c, dataContext);
        
        if (rtIds.Count == 0)
        {
            nodeContext.Error("No RtIds found");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        c.FieldFilters.GetFieldFilter(dataContext, dataQueryOperation);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(session, ckTypeId, rtIds,
            dataQueryOperation, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, r);

        await next(dataContext, nodeContext);
    }

    private static List<OctoObjectId> GetRtIds(GetRtEntitiesByIdNodeConfiguration c, IDataContext dataContext)
    {
        if(c.RtIds is { Count: > 0 })
        {
            return c.RtIds.ToList();
        }
        
        if (c.RtIdsPath != null)
        {
            var rtIds = dataContext.GetSimpleArrayValueByPath<string>(c.RtIdsPath)?.ToList();
            if (rtIds == null || rtIds.Count == 0)
            {
                throw new InvalidOperationException($"No RtIds found at path '{c.RtIdsPath}'");
            }
            return rtIds
                .Select(id => new OctoObjectId(id!))
                .ToList();
        }
        
        return [];
    }
}
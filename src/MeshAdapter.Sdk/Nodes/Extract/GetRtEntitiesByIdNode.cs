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

        var queryOptions = RtEntityQueryOptions.Create();
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

        // AB#5028 — scoped: an ordinary read of tenant business data.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(session, ckTypeId, rtIds,
            queryOptions, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.Set(c.TargetPath, r, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

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
            var rtIds = dataContext.GetArray<string>(c.RtIdsPath)?.ToList();
            if (rtIds == null || rtIds.Count == 0)
            {
                // Name the accepted path forms: the read is lenient (AB#5351), so an empty result
                // means the path matched nothing — not that the form is unsupported.
                throw new InvalidOperationException(
                    $"No RtIds found at path '{c.RtIdsPath}'. The path may address an array of ids " +
                    "(\"$.ids\"), a single id (read as one entry), or select the ids with a wildcard, " +
                    "a recursive descent or a filter (\"$.Items[*].RtId\", \"$..RtId\", " +
                    "\"$.Items[?(@.Kind=='Doc')].RtId\"). An object, a null or an absent path yields " +
                    "nothing. Inside ForEach@1 a selecting path only sees the iteration document " +
                    "(\"$.key…\", \"$.full…\"), not the outer one.");
            }
            return rtIds
                .Select(id => new OctoObjectId(id!))
                .ToList();
        }
        
        return [];
    }
}
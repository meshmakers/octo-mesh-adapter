using GraphQL.Client.Abstractions.Utilities;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
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
public class GetRtEntitiesByIdNode(NodeDelegate next, IMeshEtlContext context, ICkCacheService ckCacheService)
    : IPipelineNode
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

        // AB#9771: several child tenants, each opened by the cross-tenant guard; the result names the source tenant of
        // every item.
        if (c.TargetTenants != null)
        {
            var consolidated = await MultiTenantRead.ReadAsync(etlContext, c.TargetTenant, c.TargetTenants,
                c.Identity, dataContext, nodeContext, s => ReadAsync(s, c, ckTypeId, rtIds, dataContext, nodeContext));
            dataContext.Set(c.TargetPath, consolidated, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
            await next(dataContext, nodeContext);
            return;
        }

        // AB#9768: the own tenant, or the target the cross-tenant guard opened (a refusal ends the node here).
        var scope = await TenantReadScope.OpenAsync(etlContext, c.TargetTenant, c.Identity, dataContext, nodeContext);

        var r = await ReadAsync(scope, c, ckTypeId, rtIds, dataContext, nodeContext);

        dataContext.Set(c.TargetPath, r, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Reads the entities from one tenant: the own one or one the cross-tenant guard opened. Hidden attributes are
    /// checked and dropped with the CK model of that tenant.
    /// </summary>
    private async Task<IResultSet<RtEntity>> ReadAsync(TenantReadScope scope, GetRtEntitiesByIdNodeConfiguration c,
        RtCkId<CkTypeId> ckTypeId, List<OctoObjectId> rtIds, IDataContext dataContext, INodeContext nodeContext)
    {
        var etlContext = context;

        // AB#7768: no filter on a Hidden attribute (of the tenant the data comes from).
        await scope.EnsureCacheLoadedAsync(ckCacheService);
        RtAttributeAccessGuard.EnsureNotHidden(ckCacheService, scope.TenantId, nodeContext.NodePath.ToString(),
            [ckTypeId], (c.FieldFilters ?? []).Select(f => f.AttributePath), "field filter");

        var queryOptions = RtEntityQueryOptions.Create();
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

        // AB#5028 — scoped: an ordinary read of tenant business data.
        // AB#9768: with a target, the session the guard opened under the identity proven there.
        var session = scope.IsCrossTenant
            ? await scope.OpenTargetSessionAsync()
            : await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var r = await scope.Repository.GetRtEntitiesByIdAsync(session, ckTypeId, rtIds,
            queryOptions, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        // AB#7768: Hidden values never enter the data context.
        return await scope.DropHiddenAsync(ckCacheService, r);
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
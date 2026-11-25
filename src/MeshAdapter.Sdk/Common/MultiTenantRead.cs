using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     The multi-tenant read of GetRtEntitiesByType@1 and GetRtEntitiesById@1 (AB#9771, <c>targetTenants</c>): the same
///     selection read from several tenants of the pipeline's subtree, each one opened by the cross-tenant guard.
/// </summary>
/// <remarks>
///     Invariants:
///     <list type="bullet">
///         <item>Every target is opened with <see cref="IMeshEtlContext.OpenTenantAsync" /> as a
///         <see cref="TenantRelation.Child" /> target, also one from the registry's list of children: the guard checks
///         relation, identity proof and generation for each of them. There is no other way to another tenant.</item>
///         <item>All targets are opened before anything is read. With <see cref="CrossTenantRefusalHandling.Fail" /> a
///         single refusal throws before the first read, so no data of any tenant enters the data context.</item>
///         <item>With <see cref="CrossTenantRefusalHandling.Report" /> a refused tenant contributes nothing but its entry
///         in <c>RefusedTenants</c> (tenant id and the stable code, never the reason).</item>
///         <item>A refusal that is not about the target (system identity, invalid configuration) always fails, also with
///         <c>Report</c>: it would refuse every target, and an empty consolidation must not look like a valid one.</item>
///         <item>Errors other than a refusal (a failed query, an unreadable registry) fail the node as before.</item>
///     </list>
/// </remarks>
internal static class MultiTenantRead
{
    /// <summary>Key of the source tenant on every item of the result.</summary>
    internal const string SourceTenantIdKey = "SourceTenantId";

    /// <summary>
    ///     Opens every target, then reads each allowed one with <paramref name="readAsync" /> and returns the
    ///     multi-tenant result document.
    /// </summary>
    public static async Task<JsonObject> ReadAsync(IMeshEtlContext etlContext, TenantTargetConfiguration? targetTenant,
        TenantReadSetConfiguration set, NodeExecutionIdentity identity, IDataContext dataContext,
        INodeContext nodeContext, Func<TenantReadScope, Task<IResultSet<RtEntity>>> readAsync)
    {
        var nodePath = nodeContext.NodePath.ToString();
        var problem = CheckConfiguration(targetTenant, set);
        if (problem != null)
        {
            throw CrossTenantAccessDeniedException.InvalidConfiguration(nodePath, problem);
        }

        IReadOnlyList<string> targets = set.TenantIds != null
            ? set.TenantIds.Select(TenantRegistrySnapshot.Normalize).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : await etlContext.ListChildTenantsAsync(nodeContext);

        var opened = new List<ICrossTenantAccess>();
        var refusals = new List<CrossTenantAccessDeniedException>();
        var refusedTenantIds = new List<string>();
        foreach (var tenantId in targets)
        {
            try
            {
                opened.Add(await etlContext.OpenTenantAsync(
                    new TenantTargetConfiguration { Relation = TenantRelation.Child, TenantId = tenantId },
                    identity, dataContext, nodeContext));
            }
            catch (CrossTenantAccessDeniedException ex)
            {
                if (ex.ReasonCode is CrossTenantRefusalReason.SystemIdentityRefused
                    or CrossTenantRefusalReason.InvalidTargetConfiguration)
                {
                    throw;
                }

                refusals.Add(ex);
                refusedTenantIds.Add(tenantId);
            }
        }

        if (refusals.Count > 0 && (set.OnRefused ?? CrossTenantRefusalHandling.Fail) == CrossTenantRefusalHandling.Fail)
        {
            throw CrossTenantAccessDeniedException.ForbiddenTenants(nodePath, targets.Count, refusals,
                refusedTenantIds);
        }

        foreach (var tenantId in refusedTenantIds)
        {
            nodeContext.Warning($"Tenant '{tenantId}' was refused ({CrossTenantAccessCodes.Forbidden}); " +
                                "its data is not part of the result (onRefused: Report)");
        }

        if (targets.Count == 0)
        {
            nodeContext.Warning("The read has no target tenant (the pipeline's tenant has no child tenant)");
        }

        var items = new JsonArray();
        var tenants = new JsonArray();
        long totalCount = 0;
        foreach (var access in opened)
        {
            var result = await readAsync(TenantReadScope.FromAccess(access));
            foreach (var entity in result.Items)
            {
                var item = JsonSerializer.SerializeToNode(entity, SystemTextJsonOptions.NodeNavigation)!.AsObject();
                item[SourceTenantIdKey] = access.TenantId;
                items.Add(item);
            }

            totalCount += result.TotalCount;
            tenants.Add(new JsonObject
            {
                ["TenantId"] = access.TenantId,
                ["TotalCount"] = result.TotalCount
            });
        }

        var refused = new JsonArray();
        foreach (var tenantId in refusedTenantIds)
        {
            refused.Add(new JsonObject
            {
                ["TenantId"] = tenantId,
                ["ErrorCode"] = CrossTenantAccessCodes.Forbidden
            });
        }

        return new JsonObject
        {
            ["Items"] = items,
            ["TotalCount"] = totalCount,
            ["Tenants"] = tenants,
            ["RefusedTenants"] = refused
        };
    }

    /// <summary>The static rules of a multi-tenant read; <c>null</c> when consistent.</summary>
    internal static string? CheckConfiguration(TenantTargetConfiguration? targetTenant, TenantReadSetConfiguration set)
    {
        if (targetTenant != null)
        {
            return "set either targetTenant or targetTenants, not both";
        }

        if (set.Relation == null)
        {
            return "targetTenants needs a relation (Child)";
        }

        if (set.Relation != TenantRelation.Child)
        {
            return $"targetTenants supports only the relation Child, not {set.Relation}";
        }

        if (set.TenantIds is { Count: 0 } || set.TenantIds?.All(string.IsNullOrWhiteSpace) == true)
        {
            return "targetTenants.tenantIds is empty; leave it out to read every child tenant";
        }

        return null;
    }
}

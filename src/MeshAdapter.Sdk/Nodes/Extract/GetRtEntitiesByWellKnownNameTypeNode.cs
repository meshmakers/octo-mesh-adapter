using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByWellKnownNameNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByWellKnownNameTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <summary>Resolved write-back values for one source item, keyed by well-known name.</summary>
    private sealed record Resolution(
        string RtId, string CkTypeId, int ModOperation, IReadOnlyDictionary<string, object?>? Attributes);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByWellKnownNameNodeConfiguration>();

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        // Read array at the source path. Each item is expected to contain a wellKnownName
        // (resolvable via the inner WellKnownNamePath) plus other metadata; we'll write the
        // resolved RtId/CkTypeId/ModOperation back into each item.
        if (dataContext.GetKind(c.Path) != DataKind.Array || dataContext.Length(c.Path) == 0)
        {
            await next(dataContext, nodeContext);
            return;
        }

        var wellKnownNamePath = JsonNodePath.NormalizePathOrRelative(c.WellKnownNamePath);

        // Pass 1: collect the well-known names present on the source items (surface reads only).
        var names = new HashSet<string>();
        foreach (var item in dataContext.SelectMatches($"{c.Path}[*]"))
        {
            if (item.GetValue(wellKnownNamePath) is string name && !string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldIn(nameof(RtEntity.RtWellKnownName), names);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, queryOptions, c.Skip,
            c.Take);
        await session.CommitTransactionAsync();

        // Build the resolution map keyed by well-known name. Matched entities resolve to an
        // Update; unmatched names (when GenerateInsertOperation) resolve to a freshly-id'd Insert.
        var resolutionsByName = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        foreach (var rtEntity in r.Items)
        {
            if (rtEntity.RtWellKnownName == null || !names.Contains(rtEntity.RtWellKnownName))
            {
                continue;
            }

            resolutionsByName[rtEntity.RtWellKnownName] = new Resolution(
                rtEntity.RtId.ToString(),
                rtEntity.CkTypeId!.ToString(),
                (int)UpdateKind.Update,
                string.IsNullOrEmpty(c.AttributeTargetPath) ? null : rtEntity.Attributes.ToDictionary());
        }

        if (c.GenerateInsertOperation)
        {
            foreach (var name in names.Where(n => !resolutionsByName.ContainsKey(n)))
            {
                resolutionsByName[name] = new Resolution(
                    OctoObjectId.GenerateNewId().ToString(),
                    ckTypeId.ToString(),
                    (int)UpdateKind.Insert,
                    string.IsNullOrEmpty(c.AttributeTargetPath)
                        ? null
                        : new Dictionary<string, object?>());
            }
        }

        // Pass 2: write the resolved values back into each matching item. UpdateMatchesAsync
        // merges sub-context writes back to this context's overlay at each match's canonical
        // path — the path-only equivalent of the former in-place JsonObject mutation + array
        // write-back. Insert resolutions write RtId, ModOperation, CkTypeId (in that order to
        // match the former insert branch); update resolutions write RtId, CkTypeId,
        // ModOperation.
        await dataContext.UpdateMatchesAsync($"{c.Path}[*]", item =>
        {
            if (item.GetValue(wellKnownNamePath) is not string name || string.IsNullOrEmpty(name)
                || !resolutionsByName.TryGetValue(name, out var res))
            {
                return Task.CompletedTask;
            }

            if (res.ModOperation == (int)UpdateKind.Insert)
            {
                item.Set(c.RtIdTargetPath, res.RtId);
                item.Set(c.ModOperationPath, res.ModOperation);
                item.Set(c.CkTypeIdTargetPath, res.CkTypeId);
            }
            else
            {
                item.Set(c.RtIdTargetPath, res.RtId);
                item.Set(c.CkTypeIdTargetPath, res.CkTypeId);
                item.Set(c.ModOperationPath, res.ModOperation);
            }

            if (!string.IsNullOrEmpty(c.AttributeTargetPath) && res.Attributes != null)
            {
                item.Set(c.AttributeTargetPath!, res.Attributes);
            }

            return Task.CompletedTask;
        });

        await next(dataContext, nodeContext);
    }
}
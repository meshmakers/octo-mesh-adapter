using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

[NodeConfiguration(typeof(BackfillFromRtEntityNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class BackfillFromRtEntityNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<BackfillFromRtEntityNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.ArchiveRtId))
        {
            throw new InvalidOperationException(
                "BackfillFromRtEntity: archiveRtId is required. Configure the node with the runtime id of the CkArchive whose column spec drives the backfill.");
        }

        var updateInfos = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (updateInfos == null || updateInfos.Count == 0)
        {
            await next(dataContext, nodeContext);
            return;
        }

        var archiveRtId = new OctoObjectId(c.ArchiveRtId);
        var tenantContext = await systemContext.FindTenantContextAsync(etlContext.TenantId);
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var archive = await archiveStore.GetAsync(archiveRtId)
            ?? throw new InvalidOperationException(
                $"BackfillFromRtEntity: archive '{archiveRtId}' not found in tenant '{etlContext.TenantId}'.");

        // Empty column spec means the archive only has the standard time-series columns — no
        // user attributes to backfill, just pass through.
        if (archive.Columns.Count == 0)
        {
            await next(dataContext, nodeContext);
            return;
        }

        var columnPaths = archive.Columns.Select(col => col.Path).ToArray();

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        var entityCache = new Dictionary<OctoObjectId, RtEntity?>();
        var loadedCount = 0;
        var filledCount = 0;

        foreach (var entityUpdateInfo in updateInfos)
        {
            if (entityUpdateInfo.RtEntity == null)
            {
                continue;
            }

            var rtId = entityUpdateInfo.RtId ?? entityUpdateInfo.RtEntity.RtId;
            if (rtId == OctoObjectId.Empty)
            {
                continue;
            }

            // Determine which archive columns are still missing on this update — load from Mongo
            // only if at least one column needs backfilling.
            var missing = columnPaths
                .Where(path => !entityUpdateInfo.RtEntity.Attributes.ContainsKey(path))
                .ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            if (!entityCache.TryGetValue(rtId, out var persistedEntity))
            {
                persistedEntity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(
                    session, new RtEntityId(entityUpdateInfo.CkTypeId, rtId));
                entityCache[rtId] = persistedEntity;
                loadedCount++;
            }

            if (persistedEntity == null)
            {
                continue;
            }

            foreach (var path in missing)
            {
                var value = persistedEntity.GetAttributeValueOrDefault(path);
                if (value == null)
                {
                    continue;
                }

                entityUpdateInfo.RtEntity.SetAttributeRawValue(path, value);
                filledCount++;
            }
        }

        nodeContext.Debug(
            $"BackfillFromRtEntity: loaded {loadedCount} entities, filled {filledCount} attribute slots from archive '{archiveRtId}'.");

        // Write the mutated list back so downstream nodes see the backfilled attributes via the
        // data context (the deserialised list is a fresh object graph, not a live JToken view).
        dataContext.SetValueByPath(c.Path, updateInfos, DocumentModes.Replace, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }
}

using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

[NodeConfiguration(typeof(UpdateRtEntityIfNewerNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class UpdateRtEntityIfNewerNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<UpdateRtEntityIfNewerNodeConfiguration>();

        var input = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.InputPath,
            RtNewtonsoftSerializer.DefaultSerializer) ?? new List<EntityUpdateInfo<RtEntity>>();

        var filtered = new List<EntityUpdateInfo<RtEntity>>();
        var all = new List<EntityUpdateInfo<RtEntity>>();

        if (input.Count == 0)
        {
            WriteOutputs(dataContext, c, filtered, all);
            await next(dataContext, nodeContext);
            return;
        }

        var byCkType = input
            .Where(u => u.RtEntity != null)
            .GroupBy(u => u.CkTypeId);

        foreach (var group in byCkType)
        {
            var ckTypeId = group.Key;
            var entries = group.ToList();

            var wellKnownNames = entries
                .Select(u => u.RtEntity!.RtWellKnownName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            Dictionary<string, RtEntity> existingByName = new();
            if (wellKnownNames.Count > 0)
            {
                var queryOptions = RtEntityQueryOptions.Create()
                    .FieldIn(nameof(RtEntity.RtWellKnownName), wellKnownNames);

                var session = await etlContext.TenantRepository.GetSessionAsync();
                session.StartTransaction();
                var existing = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
                    session, ckTypeId, queryOptions, 0, wellKnownNames.Count);
                await session.CommitTransactionAsync();

                existingByName = existing.Items
                    .Where(e => !string.IsNullOrEmpty(e.RtWellKnownName))
                    .ToDictionary(e => e.RtWellKnownName!, StringComparer.Ordinal);
            }

            // Per-WKN aggregation across the input batch — Lesart D requires exactly one RT entity
            // per natural key, even when the batch contains many candidates (e.g. 96 slots × 30 days
            // for the same MP+ObisCode). Without this, every candidate would Insert a separate
            // entity in Mongo and every archive row would point at a different RtId.
            var batchByWkn = new Dictionary<string, BatchAggregate>(StringComparer.Ordinal);
            foreach (var update in entries)
            {
                var entity = update.RtEntity!;
                var wkn = entity.RtWellKnownName;

                if (string.IsNullOrEmpty(wkn))
                {
                    // No natural key — pass through as Insert on both outputs; dedup is structurally impossible.
                    filtered.Add(update);
                    all.Add(update);
                    continue;
                }

                var compValue = ExtractDateTime(entity.Attributes, c.ComparisonAttributePath);
                if (!batchByWkn.TryGetValue(wkn!, out var agg))
                {
                    // Resolve the canonical RtId for this WKN once — DB-existing wins, otherwise we
                    // pin the freshly-generated RtId of the first batch candidate. All subsequent
                    // archive rows for this WKN will reuse it.
                    var canonicalRtId = existingByName.TryGetValue(wkn!, out var dbEntity)
                        ? dbEntity.RtId
                        : entity.RtId;
                    agg = new BatchAggregate(canonicalRtId);
                    batchByWkn[wkn!] = agg;
                }

                agg.Candidates.Add((entity, compValue));
                if (compValue is not null
                    && (agg.LatestComparison is null || compValue.Value > agg.LatestComparison.Value))
                {
                    agg.LatestComparison = compValue;
                    agg.LatestEntity = entity;
                }
                else if (agg.LatestEntity is null)
                {
                    // No comparison value seen yet — keep the first candidate so the entity is at
                    // least represented downstream.
                    agg.LatestEntity = entity;
                }
            }

            foreach (var (wkn, agg) in batchByWkn)
            {
                var canonicalRtId = agg.CanonicalRtId;
                var latestEntity = agg.LatestEntity!;

                // Decide what (if anything) goes into _filteredEms (Mongo write path).
                if (existingByName.TryGetValue(wkn, out var dbEntity))
                {
                    var existingComp = ExtractDateTime(dbEntity.Attributes, c.ComparisonAttributePath);
                    if (agg.LatestComparison is null)
                    {
                        nodeContext.Warning(
                            $"UpdateRtEntityIfNewer: candidate '{wkn}' has no value at '{c.ComparisonAttributePath}'; treating as not-newer (skip).");
                    }
                    else if (existingComp is not null && agg.LatestComparison.Value <= existingComp.Value)
                    {
                        nodeContext.Debug(
                            $"UpdateRtEntityIfNewer: skipping '{wkn}' (latest batch {agg.LatestComparison:o} ≤ existing {existingComp:o}).");
                    }
                    else
                    {
                        filtered.Add(BuildUpdate(ckTypeId, canonicalRtId, wkn, latestEntity.Attributes));
                    }
                }
                else
                {
                    // First sighting — Insert with the canonical (= first-batch) RtId.
                    var insertEntity = new RtEntity(ckTypeId, canonicalRtId, latestEntity.Attributes)
                    {
                        RtWellKnownName = wkn
                    };
                    filtered.Add(EntityUpdateInfo<RtEntity>.CreateInsert(insertEntity));
                }

                // Every batch candidate goes into _allEms with the canonical RtId so the archive
                // write produces one row per slot, all linked to the same EM RtId.
                foreach (var (candidateEntity, _) in agg.Candidates)
                {
                    all.Add(BuildUpdate(ckTypeId, canonicalRtId, wkn, candidateEntity.Attributes));
                }
            }
        }

        WriteOutputs(dataContext, c, filtered, all);
        WriteFilteredAssociations(dataContext, c, filtered, nodeContext);
        await next(dataContext, nodeContext);
    }

    private static void WriteFilteredAssociations(IDataContext dataContext,
        UpdateRtEntityIfNewerNodeConfiguration c,
        List<EntityUpdateInfo<RtEntity>> filtered,
        INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(c.CandidateAssociationsInputPath))
        {
            return;
        }

        if (string.IsNullOrEmpty(c.FilteredAssociationsOutputPath))
        {
            nodeContext.Warning(
                "UpdateRtEntityIfNewer: CandidateAssociationsInputPath set but FilteredAssociationsOutputPath is not — associations dropped.");
            return;
        }

        var candidates = dataContext.GetComplexObjectByPath<List<AssociationUpdateInfo>>(
            c.CandidateAssociationsInputPath!, RtNewtonsoftSerializer.DefaultSerializer)
            ?? new List<AssociationUpdateInfo>();

        var insertedOriginRtIds = filtered
            .Where(u => u.ModOption == EntityModOptions.Insert && u.RtEntity != null)
            .Select(u => u.RtEntity!.RtId)
            .ToHashSet();

        var keptAssociations = candidates
            .Where(a => insertedOriginRtIds.Contains(a.Origin.RtId))
            .ToList();

        dataContext.SetValueByPath(c.FilteredAssociationsOutputPath!, keptAssociations,
            DocumentModes.Extend, ValueKinds.Simple, TargetValueWriteModes.Overwrite,
            RtNewtonsoftSerializer.DefaultSerializer);
    }

    /// <summary>
    /// Per-WKN aggregation state used by the intra-batch dedup. Lives only for one
    /// <see cref="ProcessObjectAsync"/> call.
    /// </summary>
    private sealed class BatchAggregate
    {
        public BatchAggregate(OctoObjectId canonicalRtId)
        {
            CanonicalRtId = canonicalRtId;
        }

        /// <summary>RtId every <c>_allEms</c> entry for this WKN must use (DB-existing wins).</summary>
        public OctoObjectId CanonicalRtId { get; }

        /// <summary>Entity carrying the latest comparison value seen so far.</summary>
        public RtEntity? LatestEntity { get; set; }

        /// <summary>Latest comparison value seen so far.</summary>
        public DateTime? LatestComparison { get; set; }

        /// <summary>Every batch candidate for this WKN (used to populate <c>_allEms</c>).</summary>
        public List<(RtEntity Entity, DateTime? Comparison)> Candidates { get; } = new();
    }

    private static EntityUpdateInfo<RtEntity> BuildUpdate(
        RtCkId<CkTypeId> ckTypeId,
        OctoObjectId existingRtId,
        string rtWellKnownName,
        IReadOnlyDictionary<string, object?> attributes)
    {
        var entity = new RtEntity(ckTypeId, existingRtId, attributes)
        {
            RtWellKnownName = rtWellKnownName
        };
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(ckTypeId, existingRtId), entity);
    }

    private static void WriteOutputs(IDataContext dataContext,
        UpdateRtEntityIfNewerNodeConfiguration c,
        List<EntityUpdateInfo<RtEntity>> filtered,
        List<EntityUpdateInfo<RtEntity>> all)
    {
        dataContext.SetValueByPath(c.FilteredOutputPath, filtered, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite, RtNewtonsoftSerializer.DefaultSerializer);
        dataContext.SetValueByPath(c.OutputPathAll, all, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite, RtNewtonsoftSerializer.DefaultSerializer);
    }

    private static DateTime? ExtractDateTime(IReadOnlyDictionary<string, object?> attributes, string path)
    {
        var parts = path.Split('.');
        object? current = attributes;
        foreach (var part in parts)
        {
            switch (current)
            {
                case IReadOnlyDictionary<string, object?> ro:
                    if (!ro.TryGetValue(part, out current)) return null;
                    break;
                case IDictionary<string, object?> rw:
                    if (!rw.TryGetValue(part, out current)) return null;
                    break;
                case RtTypeWithAttributes typed:
                    // Record-typed attribute (RtRecord) or RtEntity — step into the typed
                    // Attributes map so the documented "Record.Field" syntax works.
                    if (!typed.Attributes.TryGetValue(part, out current)) return null;
                    break;
                case Newtonsoft.Json.Linq.JObject jo:
                    var token = jo[part];
                    if (token == null) return null;
                    current = token.Type == Newtonsoft.Json.Linq.JTokenType.Object ? token : token.ToObject<object?>();
                    break;
                default:
                    return null;
            }

            if (current == null) return null;
        }

        // Normalise to UTC so candidate-vs-DB comparisons are independent of the kind/offset
        // each side happens to carry. Unspecified kind is treated as UTC to match the
        // AssumeUniversal/AdjustToUniversal semantics applied to string values below.
        return current switch
        {
            DateTime dt => dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            },
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) => parsed,
            _ => null
        };
    }
}

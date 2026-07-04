using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Resolves a mapping export document (see <see cref="ExportDataPointMappingsNode"/>)
/// back to runtime entities and emits importable mapping suggestions.
///
/// Per endpoint the resolution order is: RtId (same-tenant shortcut) →
/// identity attribute value (survives tenant re-initialisation) → unique
/// entity name. Entries whose endpoints cannot be resolved unambiguously are
/// reported in the statistics — never guessed.
///
/// The output array shape is a superset of the GenerateDataPointMappings /
/// AnthropicAiQuery suggestion contract (adds <c>enabled</c>), so the same
/// downstream ForEach (GetOrCreate by name + CreateUpdateInfo +
/// CreateAssociationUpdate + ApplyChanges) persists imported mappings too.
/// </summary>
[NodeConfiguration(typeof(ImportDataPointMappingsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class ImportDataPointMappingsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ImportDataPointMappingsNodeConfiguration>();
        var document = dataContext.Get<DataPointMappingExportDocument>(c.Path);

        var suggestions = new List<ImportedMappingSuggestion>();
        var unresolved = new List<UnresolvedImportEntry>();

        if (document == null || document.Mappings.Count == 0)
        {
            nodeContext.Warning($"No mapping export document found at '{c.Path}' — nothing to import.");
            WriteResults(dataContext, c, suggestions, unresolved, 0);
            await next(dataContext, nodeContext);
            return;
        }

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Load every CK type referenced by any endpoint exactly once and index it.
        var indices = new Dictionary<string, EntityIndex>(StringComparer.Ordinal);
        var referencedTypes = document.Mappings
            .SelectMany(m => new[] { m.Source?.CkTypeId, m.Target?.CkTypeId })
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal);
        foreach (var ckTypeId in referencedTypes)
        {
            var items = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
                session, new RtCkId<CkTypeId>(ckTypeId), RtEntityQueryOptions.Create())).Items.ToList();
            indices[ckTypeId] = new EntityIndex(items);
        }

        foreach (var mapping in document.Mappings)
        {
            var source = ResolveEndpoint(mapping.Source, indices, out var sourceFailure);
            var target = ResolveEndpoint(mapping.Target, indices, out var targetFailure);

            if (source == null || target == null)
            {
                var reasons = new List<string>();
                if (source == null) reasons.Add($"source: {sourceFailure}");
                if (target == null) reasons.Add($"target: {targetFailure}");
                unresolved.Add(new UnresolvedImportEntry(mapping.Name, string.Join("; ", reasons)));
                continue;
            }

            suggestions.Add(new ImportedMappingSuggestion(
                mapping.Name,
                source.Entity.RtId.ToString(),
                mapping.Source!.CkTypeId,
                target.Entity.RtId.ToString(),
                mapping.Target!.CkTypeId,
                mapping.SourceAttributePath,
                mapping.TargetAttributePath,
                mapping.MappingExpression,
                "import",
                $"Import: source via {source.ResolvedBy}, target via {target.ResolvedBy}",
                mapping.Enabled));
        }

        WriteResults(dataContext, c, suggestions, unresolved, document.Mappings.Count);

        nodeContext.Info(
            $"Resolved {suggestions.Count} of {document.Mappings.Count} imported mappings.");
        if (unresolved.Count > 0)
        {
            nodeContext.Warning(
                $"{unresolved.Count} mapping(s) could not be resolved: " +
                string.Join(", ", unresolved.Take(10).Select(u => u.Name)) +
                (unresolved.Count > 10 ? ", …" : ""));
        }

        await next(dataContext, nodeContext);
    }

    private static void WriteResults(
        IDataContext dataContext,
        ImportDataPointMappingsNodeConfiguration c,
        List<ImportedMappingSuggestion> suggestions,
        List<UnresolvedImportEntry> unresolved,
        int total)
    {
        dataContext.Set(c.TargetPath, suggestions, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);
        if (!string.IsNullOrWhiteSpace(c.StatisticsTargetPath))
        {
            var statistics = new ImportStatistics(total, suggestions.Count, unresolved.Count, unresolved);
            dataContext.Set(c.StatisticsTargetPath, statistics, c.DocumentMode, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
        }
    }

    /// <summary>
    /// Resolves one endpoint reference against the pre-built per-type index.
    /// Returns null with a human-readable <paramref name="failureReason"/> when
    /// no unambiguous entity is found.
    /// </summary>
    private static ResolvedEndpoint? ResolveEndpoint(
        ExportedEntityRef? reference,
        IReadOnlyDictionary<string, EntityIndex> indices,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (reference == null)
        {
            failureReason = "no endpoint reference in the export document";
            return null;
        }

        if (!indices.TryGetValue(reference.CkTypeId, out var index) || index.IsEmpty)
        {
            failureReason = $"no entities of type '{reference.CkTypeId}' on this tenant";
            return null;
        }

        // 1. RtId — valid only when importing into the exporting tenant.
        if (!string.IsNullOrWhiteSpace(reference.RtId) &&
            index.ByRtId.TryGetValue(reference.RtId, out var byRtId))
        {
            return new ResolvedEndpoint(byRtId, "rtId");
        }

        // 2. Identity attribute — the portable natural key.
        if (!string.IsNullOrWhiteSpace(reference.IdentityAttribute) &&
            !string.IsNullOrWhiteSpace(reference.IdentityValue))
        {
            var matches = index.ByAttribute(reference.IdentityAttribute!, reference.IdentityValue!);
            switch (matches.Count)
            {
                case 1:
                    return new ResolvedEndpoint(matches[0], $"{reference.IdentityAttribute}='{reference.IdentityValue}'");
                case > 1:
                    failureReason =
                        $"identity {reference.IdentityAttribute}='{reference.IdentityValue}' is ambiguous ({matches.Count} matches)";
                    return null;
            }
        }

        // 3. Unique name — last resort.
        if (!string.IsNullOrWhiteSpace(reference.Name))
        {
            var matches = index.ByAttribute("Name", reference.Name!);
            switch (matches.Count)
            {
                case 1:
                    return new ResolvedEndpoint(matches[0], $"name='{reference.Name}'");
                case > 1:
                    failureReason = $"name '{reference.Name}' is ambiguous ({matches.Count} matches)";
                    return null;
            }
        }

        failureReason =
            $"no match for rtId '{reference.RtId}'" +
            (reference.IdentityValue != null
                ? $", {reference.IdentityAttribute}='{reference.IdentityValue}'"
                : string.Empty) +
            (reference.Name != null ? $", name='{reference.Name}'" : string.Empty);
        return null;
    }

    private sealed record ResolvedEndpoint(RtEntity Entity, string ResolvedBy);

    /// <summary>
    /// Per-CK-type entity lookup: by RtId, and lazily by any attribute value
    /// (used for the identity attribute and the Name fallback).
    /// </summary>
    private sealed class EntityIndex
    {
        private readonly List<RtEntity> _all;
        private readonly Dictionary<string, Dictionary<string, List<RtEntity>>> _byAttribute =
            new(StringComparer.Ordinal);

        public EntityIndex(List<RtEntity> entities)
        {
            _all = entities;
            ByRtId = entities.ToDictionary(e => e.RtId.ToString(), StringComparer.Ordinal);
        }

        public Dictionary<string, RtEntity> ByRtId { get; }

        public bool IsEmpty => _all.Count == 0;

        public IReadOnlyList<RtEntity> ByAttribute(string attribute, string value)
        {
            if (!_byAttribute.TryGetValue(attribute, out var lookup))
            {
                lookup = new Dictionary<string, List<RtEntity>>(StringComparer.Ordinal);
                foreach (var entity in _all)
                {
                    var raw = entity.GetAttributeValueOrDefault(attribute);
                    var key = raw as string ?? raw?.ToString();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!lookup.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<RtEntity>();
                        lookup[key] = bucket;
                    }
                    bucket.Add(entity);
                }
                _byAttribute[attribute] = lookup;
            }

            return lookup.TryGetValue(value, out var matches)
                ? matches
                : Array.Empty<RtEntity>();
        }
    }

    /// <summary>
    /// Superset of the GenerateDataPointMappings suggestion contract: same key
    /// names/order plus <c>enabled</c>, so the shared downstream ForEach can
    /// persist imported mappings (and preserve their enabled flag when the
    /// pipeline template references <c>$.enabled</c>).
    /// </summary>
    internal sealed record ImportedMappingSuggestion(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("controlRtId")] string ControlRtId,
        [property: JsonPropertyName("controlCkTypeId")] string ControlCkTypeId,
        [property: JsonPropertyName("spaceRtId")] string SpaceRtId,
        [property: JsonPropertyName("spaceCkTypeId")] string SpaceCkTypeId,
        [property: JsonPropertyName("sourceAttributePath")] string SourceAttributePath,
        [property: JsonPropertyName("targetAttributePath")] string TargetAttributePath,
        [property: JsonPropertyName("mappingExpression")] string MappingExpression,
        [property: JsonPropertyName("ruleId")] string RuleId,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("enabled")] bool Enabled);

    /// <summary>Statistics written to <c>StatisticsTargetPath</c>.</summary>
    internal sealed record ImportStatistics(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("resolved")] int Resolved,
        [property: JsonPropertyName("unresolved")] int Unresolved,
        [property: JsonPropertyName("unresolvedEntries")] IReadOnlyList<UnresolvedImportEntry> UnresolvedEntries);

    /// <summary>One entry the import could not resolve, for manual follow-up.</summary>
    internal sealed record UnresolvedImportEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("reason")] string Reason);
}

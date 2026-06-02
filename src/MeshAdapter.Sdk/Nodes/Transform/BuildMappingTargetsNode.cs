using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Resolves all active DataPointMappings and produces a list of external identifiers
/// (mapping targets) from their source entities. Works generically for any adapter type
/// that uses DataPointMapping entities for data acquisition configuration.
///
/// Output format per target:
/// - Plain identifier string: when sourceAttributePath is empty or matches the default
/// - Pipe-separated triple "identifier|stateName|stateId": when the mapping references
///   a specific sub-state on the source entity
/// </summary>
[NodeConfiguration(typeof(BuildMappingTargetsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class BuildMappingTargetsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private static readonly RtCkId<CkTypeId> DataPointMappingCkTypeId = new("System.Communication/DataPointMapping");
    private static readonly RtCkId<CkAssociationRoleId> MapsFromRoleId = new("System.Communication/MapsFrom");

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<BuildMappingTargetsNodeConfiguration>();
        var sourceCkTypeId = new RtCkId<CkTypeId>(c.SourceCkTypeId);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Load all enabled DataPointMappings
        var mappingsResult = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, DataPointMappingCkTypeId, RtEntityQueryOptions.Create());

        var targets = new List<MappingTargetRecord>();
        var seen = new HashSet<string>();

        foreach (var mapping in mappingsResult.Items)
        {
            var enabled = mapping.GetAttributeValueOrDefault<bool>("Enabled") ?? true;
            if (!enabled) continue;

            var sourceAttributePath = mapping.GetAttributeValueOrDefault("SourceAttributePath") as string;

            // Find the source entity via outbound MapsFrom
            var sourceAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
                session,
                new RtEntityId(DataPointMappingCkTypeId, mapping.RtId),
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, MapsFromRoleId,
                    targetTypeId: sourceCkTypeId));

            var sourceAssoc = sourceAssocs.Items.FirstOrDefault();
            if (sourceAssoc == null) continue;

            // Load the source entity
            var sourceResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                session, sourceCkTypeId, new[] { sourceAssoc.TargetRtId },
                RtEntityQueryOptions.Create());

            var source = sourceResult.Items.FirstOrDefault();
            if (source == null) continue;

            var identifier = source.GetAttributeValueOrDefault(c.SourceIdentifierAttribute) as string;
            if (string.IsNullOrWhiteSpace(identifier)) continue;

            // Default: plain identifier (no sub-state)
            if (string.IsNullOrWhiteSpace(sourceAttributePath) ||
                string.Equals(sourceAttributePath, c.DefaultAttributePath, StringComparison.OrdinalIgnoreCase))
            {
                var key = identifier;
                if (seen.Add(key))
                {
                    targets.Add(CreateMappingTargetRecord(identifier, null, identifier));
                }

                continue;
            }

            // Sub-state resolution (only when StatesAttribute is configured)
            if (string.IsNullOrWhiteSpace(c.StatesAttribute) ||
                string.IsNullOrWhiteSpace(c.StateKeyAttribute) ||
                string.IsNullOrWhiteSpace(c.StateValueAttribute))
            {
                var key = $"{identifier}:{sourceAttributePath}";
                if (seen.Add(key))
                {
                    targets.Add(CreateMappingTargetRecord(identifier, sourceAttributePath, identifier));
                }

                continue;
            }

            // Look up the state in the RecordArray
            var statesValue = source.GetAttributeValueOrDefault(c.StatesAttribute);
            // Support both IEnumerable<RtRecord> and generic IEnumerable (List<object> with RtRecord items)
            var stateRecords = statesValue switch
            {
                IEnumerable<RtRecord> records => records.ToList(),
                System.Collections.IEnumerable enumerable => enumerable.OfType<RtRecord>().ToList(),
                _ => null
            };

            if (stateRecords == null || stateRecords.Count == 0)
            {
                nodeContext.Warning(
                    $"Source {source.RtId}: no states records in '{c.StatesAttribute}' (type: {statesValue?.GetType().Name ?? "null"}) — using plain identifier");
                var key = identifier;
                if (seen.Add(key))
                {
                    targets.Add(CreateMappingTargetRecord(identifier, null, identifier));
                }

                continue;
            }

            string? stateId = null;
            foreach (var stateRecord in stateRecords)
            {
                var stateKey = stateRecord.GetAttributeValueOrDefault(c.StateKeyAttribute) as string;
                if (string.Equals(stateKey, sourceAttributePath, StringComparison.OrdinalIgnoreCase))
                {
                    stateId = stateRecord.GetAttributeValueOrDefault(c.StateValueAttribute) as string;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(stateId))
            {
                nodeContext.Warning(
                    $"Source {source.RtId}: state '{sourceAttributePath}' not found in '{c.StatesAttribute}'");
                continue;
            }

            var dedupeKey = $"{identifier}:{sourceAttributePath}:{stateId}";
            if (seen.Add(dedupeKey))
            {
                targets.Add(CreateMappingTargetRecord(identifier, sourceAttributePath, stateId));
            }
        }

        dataContext.Set(c.TargetPath, targets, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        nodeContext.Info($"Built {targets.Count} mapping targets from DataPointMappings");

        await next(dataContext, nodeContext);
    }

    internal static MappingTargetRecord CreateMappingTargetRecord(string sourceIdentifier, string? stateName,
        string externalId) =>
        new(
            new RecordTypeRef("System.Communication/MappingTarget"),
            // Name is added after ExternalId in the legacy JsonObject only when present;
            // the property-level WhenWritingNull override omits the key when stateName is null
            // (the pipeline default preserves nulls, so the override is required for parity).
            new MappingTargetAttributes(sourceIdentifier, externalId,
                string.IsNullOrWhiteSpace(stateName) ? null : stateName));

    /// <summary>CK RecordArray item: <c>{ "CkRecordId": {...}, "Attributes": {...} }</c>.</summary>
    internal sealed record MappingTargetRecord(
        [property: JsonPropertyName("CkRecordId")] RecordTypeRef CkRecordId,
        [property: JsonPropertyName("Attributes")] MappingTargetAttributes Attributes);

    internal sealed record RecordTypeRef(
        [property: JsonPropertyName("SemanticVersionedFullName")] string SemanticVersionedFullName);

    internal sealed record MappingTargetAttributes(
        [property: JsonPropertyName("SourceIdentifier")] string SourceIdentifier,
        [property: JsonPropertyName("ExternalId")] string ExternalId,
        [property: JsonPropertyName("Name")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name);
}

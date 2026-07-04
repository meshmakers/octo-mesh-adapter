using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the ExportDataPointMappings node.
///
/// The node serialises the tenant's DataPointMapping entities into a portable
/// document that survives tenant re-initialisation: instead of raw RtIds, every
/// mapping endpoint carries a NATURAL identity — a configurable identity
/// attribute per CK type (e.g. Loxone/Control → LoxoneUuid, MQTT/Topic →
/// TopicPath) plus the entity name as fallback. RtIds are included as a hint
/// for same-tenant round trips but are never required on import.
///
/// The counterpart <see cref="ImportDataPointMappingsNodeConfiguration"/> node
/// resolves the identities back to entities and emits the standard
/// GenerateDataPointMappings suggestion shape for the shared downstream
/// persistence pipeline.
/// </summary>
[NodeName("ExportDataPointMappings", 1)]
public record ExportDataPointMappingsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// CkTypeId of the mapping entities to export.
    /// </summary>
    [PropertyGroup("Mapping", 0)]
    public string MappingCkTypeId { get; set; } = "System.Communication/DataPointMapping";

    /// <summary>
    /// AssociationRoleId connecting a mapping to its source entity (outbound).
    /// </summary>
    [PropertyGroup("Mapping", 1)]
    public string MapsFromRoleId { get; set; } = "System.Communication/MapsFrom";

    /// <summary>
    /// AssociationRoleId connecting a mapping to its target entity (outbound).
    /// </summary>
    [PropertyGroup("Mapping", 2)]
    public string MapsToRoleId { get; set; } = "System.Communication/MapsTo";

    /// <summary>
    /// Identity attribute per CK type: which attribute on an endpoint entity
    /// carries its stable natural key (e.g. Loxone/Control → LoxoneUuid,
    /// EnergyIQ types → GlobalId). Types without an entry export name-only
    /// identities (resolved by unique name on import).
    /// </summary>
    [PropertyGroup("Identity", 0)]
    public ICollection<EntityIdentityAttributeConfiguration> IdentityAttributes { get; set; } =
        new List<EntityIdentityAttributeConfiguration>();

    /// <summary>
    /// Optional regular expression: mappings whose Name matches are skipped.
    /// Use this to export only the MANUAL delta — rule-generated mappings
    /// follow the deterministic "ruleId|rtId|state" pattern (e.g.
    /// "^[\\w-]+\\|[0-9a-f]{24}\\|") and are reproducible by re-running the
    /// generation pipeline, so they don't need to be part of the export.
    /// </summary>
    [PropertyGroup("Filter", 0)]
    public string? ExcludeNameRegex { get; set; }

    /// <summary>
    /// Whether disabled mappings are exported too. Default true — an export is
    /// a backup, and a deliberately disabled mapping is still configuration.
    /// </summary>
    [PropertyGroup("Filter", 1)]
    public bool IncludeDisabled { get; set; } = true;
}

/// <summary>
/// One (CK type → identity attribute) entry for mapping export/import.
/// </summary>
public record EntityIdentityAttributeConfiguration
{
    /// <summary>
    /// Runtime CK type id the entry applies to (e.g. "Loxone/Control").
    /// </summary>
    public required string CkTypeId { get; set; }

    /// <summary>
    /// Attribute on entities of that type holding the stable natural key
    /// (e.g. "LoxoneUuid", "TopicPath", "NodeId", "GlobalId").
    /// </summary>
    public required string Attribute { get; set; }
}

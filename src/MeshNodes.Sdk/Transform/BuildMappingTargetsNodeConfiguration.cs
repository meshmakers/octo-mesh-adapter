using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the BuildMappingTargets node.
/// Resolves all active DataPointMappings and extracts external identifiers from their
/// source entities. Produces a list of mapping targets that an external adapter can use
/// to acquire data (e.g. poll, subscribe, read).
///
/// Each target is either:
/// - A plain identifier string (when sourceAttributePath is empty or matches default)
/// - A pipe-separated triple "identifier|stateName|stateId" (when the source entity has
///   sub-states and sourceAttributePath references a specific state)
///
/// Works generically for Loxone, MQTT, OPC-UA, or any adapter that uses DataPointMapping.
/// </summary>
[NodeName("BuildMappingTargets", 1)]
public record BuildMappingTargetsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// CkTypeId of the source entities that DataPointMappings map from
    /// (e.g. "Loxone/Control", "MQTT/Topic", "OpcUa/Node").
    /// </summary>
    [PropertyGroup("Source", 0)]
    public required string SourceCkTypeId { get; set; }

    /// <summary>
    /// Attribute name on the source entity that holds the external identifier
    /// (e.g. "LoxoneUuid", "TopicPath", "NodeId").
    /// </summary>
    [PropertyGroup("Source", 1)]
    public required string SourceIdentifierAttribute { get; set; }

    /// <summary>
    /// Optional: Attribute name (RecordArray) on the source entity holding sub-states.
    /// When set, the node looks up sourceAttributePath in this record array.
    /// If not set, only plain identifiers are produced.
    /// </summary>
    [PropertyGroup("States", 0)]
    public string? StatesAttribute { get; set; }

    /// <summary>
    /// Attribute name within each state record that holds the state name/key
    /// (e.g. "StateName", "PropertyName"). Required when StatesAttribute is set.
    /// </summary>
    [PropertyGroup("States", 1)]
    public string? StateKeyAttribute { get; set; }

    /// <summary>
    /// Attribute name within each state record that holds the state identifier/UUID
    /// (e.g. "StateUuid", "PropertyNodeId"). Required when StatesAttribute is set.
    /// </summary>
    [PropertyGroup("States", 2)]
    public string? StateValueAttribute { get; set; }

    /// <summary>
    /// The default attribute path name that maps to the source entity's main value
    /// (e.g. "currentValue"). When sourceAttributePath equals this, a plain identifier
    /// is produced instead of a state-specific triple.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public string DefaultAttributePath { get; set; } = "currentValue";
}

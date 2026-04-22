using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the ApplyDataPointMappings node.
/// Evaluates DataPointMapping entities associated with a source entity
/// and produces update items for the mapped target entities.
/// </summary>
[NodeName("ApplyDataPointMappings", 1)]
public record ApplyDataPointMappingsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// JSON path to the source entity's RtId
    /// </summary>
    [PropertyGroup("Source Entity", 0, "jsonpath")]
    public string? SourceRtIdPath { get; set; }

    /// <summary>
    /// JSON path to the source entity's CkTypeId
    /// </summary>
    [PropertyGroup("Source Entity", 1, "jsonpath")]
    public string? SourceCkTypeIdPath { get; set; }

    /// <summary>
    /// JSON path to the source value (e.g. the polled sensor value).
    /// This value is used as the default when no SourceAttributePath is configured on a mapping,
    /// and is also available as the 'value' variable in mapping expressions.
    /// </summary>
    [PropertyGroup("Source Entity", 2, "jsonpath")]
    public string? SourceValuePath { get; set; }

    /// <summary>
    /// Optional JSON path to the incoming state name (e.g. "tempActual"). When set, only
    /// mappings whose SourceAttributePath equals this value are applied. Allows multi-state
    /// sources like Loxone controls with many states to route to specific mappings.
    /// If null or empty: all mappings for the source entity are applied (legacy behavior).
    /// </summary>
    [PropertyGroup("Source Entity", 3, "jsonpath")]
    public string? SourceStateNamePath { get; set; }
}

using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Maps a value to another value by a list of mapping entries. A <c>path</c> that addresses one value
/// maps that value and writes the result to <c>targetPath</c>. A <c>path</c> that can address several
/// values (a wildcard such as <c>$.Rows[*].Values[5]</c>, a recursive descent <c>$..unit</c> or a filter)
/// maps every addressed element in place, without a loop and without a script: <c>targetPath</c> is then
/// the same path or left out, a different one is rejected, null elements are skipped, elements without a
/// matching mapping stay as they are, and a path that matches nothing only logs a warning.
/// </summary>
[NodeName("DataMapping", 1)]
public record DataMappingNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The target value type
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public required AttributeValueTypesDto TargetValueType { get; set; }

    /// <summary>
    /// The source value type
    /// </summary>
    [PropertyGroup("Data Mapping", 1)]
    public required AttributeValueTypesDto SourceValueType { get; set; }

    /// <summary>
    /// Defines the target type
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    [PropertyGroup("Data Mapping", 2)]
    public required List<MappingEntry> Mappings { get; set; }
}

/// <summary>
/// Defines a single mapping entry
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record MappingEntry
{
    /// <summary>
    /// The source value
    /// </summary>
    public required object SourceValue { get; set; }
    
    /// <summary>
    /// The target value
    /// </summary>
    public required object TargetValue { get; set; }
    
    /// <summary>
    /// An optional description. This description is only added to the debug output of the node.
    /// </summary>
    public string? Description { get; set; }
}
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("DataMapping", 1)]
public record DataMappingNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The target value type
    /// </summary>
    public required AttributeValueTypesDto TargetValueType { get; set; }
    
    /// <summary>
    /// The source value type
    /// </summary>
    public required AttributeValueTypesDto SourceValueType { get; set; }

    /// <summary>
    /// Defines the target type
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
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
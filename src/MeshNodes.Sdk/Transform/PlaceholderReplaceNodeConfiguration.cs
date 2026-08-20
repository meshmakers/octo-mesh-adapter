using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Node configuration to search for placeholders and replace them with values
/// </summary>
[NodeName("PlaceholderReplace", 1)]
public record PlaceholderReplaceNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Defines mappings for placeholders and their values
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    [PropertyGroup("Data Mapping", 0)]
    public required List<PlaceholderRule> ReplaceRules { get; set; }
}

/// <summary>
/// Defines a single mapping entry
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record PlaceholderRule
{
    /// <summary>
    /// The source value
    /// </summary>
    public required string Placeholder { get; set; }
    
    /// <summary>
    /// The source path value
    /// </summary>
    public required string Path { get; set; }
}
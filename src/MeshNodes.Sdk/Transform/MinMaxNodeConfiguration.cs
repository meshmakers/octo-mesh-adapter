using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Defines whether the node should find the minimum or maximum value.
/// </summary>
public enum MinMaxMode
{
    /// <summary>
    /// Find the object with the minimum value.
    /// </summary>
    Min,

    /// <summary>
    /// Find the object with the maximum value.
    /// </summary>
    Max
}

/// <summary>
/// Configuration node object for finding the item with the minimum or maximum value within an array.
/// Supported value types are int (long), double, and DateTime.
/// </summary>
[NodeName("MinMax", 1)]
public record MinMaxNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The path to the value used for comparison within each object in the array.
    /// This value must be a numeric type (int, double) or DateTime.
    /// </summary>
    [PropertyGroup("Query", 0, "jsonpath")]
    public required string ValuePath { get; set; }

    /// <summary>
    /// The mode of operation: find the minimum or maximum value. Defaults to <see cref="MinMaxMode.Min"/>.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public MinMaxMode Mode { get; set; } = MinMaxMode.Min;
}

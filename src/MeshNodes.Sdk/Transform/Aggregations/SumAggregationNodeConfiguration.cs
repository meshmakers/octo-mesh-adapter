using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform.Aggregations;

/// <summary>
/// Node configuration for sum aggregation, that sums values from a specified path and writes the result to a target path.
/// </summary>
[NodeName("SumAggregation", 1)]
public record SumAggregationNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    ///  List of aggregation items that specify the paths to the values to be summed and their respective multipliers.
    /// </summary>
    public required IEnumerable<SumAggregationItem> Aggregations { get; init; }
}

/// <summary>
/// An item in the sum aggregation configuration that specifies the path to the values to be summed.
/// </summary>
public record SumAggregationItem
{
    /// <summary>
    /// Path to the source objects where the values to be summed are located.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The filter path to apply to the source objects before summing the values.
    /// </summary>
    public required string? FilterPath { get; init; }

    /// <summary>
    /// The comparison operator to be used for filtering the source objects.
    /// </summary>
    public required object? ComparisonValue { get; init; }

    /// <summary>
    /// Path relative to Path to the source objects where the values to be summed are located.
    /// </summary>
    public required string AggregationPath { get; init; }

    /// <summary>
    /// Value the aggregation should multiply with before summing up the values.
    /// </summary>
    public required double Value { get; init; }
}
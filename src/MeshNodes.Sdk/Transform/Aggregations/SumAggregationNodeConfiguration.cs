using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform.Aggregations;

/// <summary>
/// Node configuration for sum aggregation, that sums values from a specified path and writes the result to a target path.
/// </summary>
[NodeName("SumAggregation", 1)]
public record SumAggregationNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Path relative to Path to the source objects where the values to be summed are located.
    /// </summary>
    public required string AggregationPath { get; init; }
}
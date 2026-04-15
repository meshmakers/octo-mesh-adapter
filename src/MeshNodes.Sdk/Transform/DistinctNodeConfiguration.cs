using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for removing duplicate objects from an array based on a unique attribute
/// </summary>
[NodeName("Distinct", 1)]
public record DistinctNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The path to the value that defines uniqueness within the array.
    /// This value must be a simple type (int, double, string, bool, datetime).
    /// </summary>
    [PropertyGroup("Query", 0, "jsonpath")]
    public required string DistinctValuePath { get; set; }
}

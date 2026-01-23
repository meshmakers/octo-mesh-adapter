using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node to get pipeline configuration by well known name
/// </summary>
[NodeName("GetPipelineConfigByWellKnownName", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetPipelineConfigByWellKnownNameNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// The well known name of the pipeline configuration (static value)
    /// </summary>
    public string? WellKnownName { get; init; }

    /// <summary>
    /// JSON path to get the well known name from the input data
    /// </summary>
    public string? WellKnownNamePath { get; init; }
}

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
    /// The well known name of the pipeline configuration 
    /// </summary>
    public required string WellKnownName { get; init; }
}

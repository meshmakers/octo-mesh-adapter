using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node to get pipeline configuration by CkTypeId
/// </summary>
[NodeName("GetPipelineConfigByCkTypeId", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetPipelineConfigByCkTypeIdNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// The CkTypeId to search for
    /// </summary>
    public required string CkTypeId { get; init; }
}

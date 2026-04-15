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
    /// The CkTypeId to search for (static value)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public string? CkTypeId { get; init; }

    /// <summary>
    /// JSON path to get the CkTypeId from the input data
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; init; }
}

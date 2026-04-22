using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for the DeployPipeline node.
/// Deploys a specific pipeline within the same data flow to its assigned adapter
/// via the Communication Controller API.
/// The target pipeline must belong to the same data flow as the executing pipeline,
/// and must not be the executing pipeline itself.
/// </summary>
[NodeName("DeployPipeline", 1)]
public record DeployPipelineNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// The fixed pipeline RtId to deploy.
    /// </summary>
    [PropertyGroup("Pipeline", 0)]
    public OctoObjectId? PipelineRtId { get; set; }

    /// <summary>
    /// JSON path to the pipeline RtId to deploy.
    /// </summary>
    [PropertyGroup("Pipeline", 1, "jsonpath")]
    public string? PipelineRtIdPath { get; set; }

    /// <summary>
    /// Well-known name of the ServiceAccountConfiguration entity for authentication.
    /// Defaults to "ServiceAccountConfig".
    /// </summary>
    [PropertyGroup("Authentication", 0)]
    public string ServiceAccountConfigName { get; set; } = "ServiceAccountConfig";
}

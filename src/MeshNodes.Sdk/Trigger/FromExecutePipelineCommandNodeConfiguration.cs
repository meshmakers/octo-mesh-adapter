using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromExecutePipelineCommand
/// </summary>
[NodeName("FromExecutePipelineCommand", 1)]
public record FromExecutePipelineCommandNodeConfiguration : TriggerNodeConfiguration;
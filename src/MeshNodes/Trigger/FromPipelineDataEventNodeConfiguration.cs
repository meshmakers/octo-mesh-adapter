using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromPipelineDataEvent
/// </summary>
[NodeName("FromPipelineDataEvent", 1)]
public record FromPipelineDataEventNodeConfiguration : TriggerNodeConfiguration;
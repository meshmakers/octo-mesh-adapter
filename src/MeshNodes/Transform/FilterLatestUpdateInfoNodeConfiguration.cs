using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for filtering the latest update info
/// </summary>
[NodeName("FilterLatestUpdateInfo", 1)]
public record FilterLatestUpdateInfoNodeConfiguration : SourceTargetPathNodeConfiguration;

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for computing a SHA-256 hash of base64-encoded file data
/// </summary>
[NodeName("ComputeFileHash", 1)]
public record ComputeFileHashNodeConfiguration : SourceTargetPathNodeConfiguration;

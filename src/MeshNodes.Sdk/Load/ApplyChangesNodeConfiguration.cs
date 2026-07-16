using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeDeprecated("Do not use this old version!")]
[NodeName("ApplyChanges", 1)]
public record ApplyChangesNodeConfiguration : PathNodeConfiguration;
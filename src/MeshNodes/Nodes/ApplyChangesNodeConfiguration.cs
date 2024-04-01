using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshNodes.Nodes;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("ApplyChanges", 1)]
public class ApplyChangesNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }
}
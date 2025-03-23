using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("ApplyChanges", 2)]
public record ApplyChangesNodeConfiguration2 : NodeConfiguration
{
    /// <summary>
    /// The path to the entity update
    /// </summary>
    public string? EntityUpdatesPath { get; init; }
    
    /// <summary>
    /// The path to the association update
    /// </summary>
    public string? AssociationUpdatesPath { get; init; }
}
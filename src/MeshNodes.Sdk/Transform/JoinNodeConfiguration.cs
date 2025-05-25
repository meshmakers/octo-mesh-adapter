using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Import from excel node
/// </summary>
[NodeName("Join", 1)]
public record JoinNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Key path in the source data to match against the join path
    /// </summary>
    public required string KeyPath { get; set; }

    /// <summary>
    /// The path to an array of items to join with the source data
    /// </summary>
    public required string JoinPath { get; set; }

    /// <summary>
    /// The path to the key in the join array that will be used to match against the source data
    /// </summary>
    public required string JoinKeyPath { get; set; }

    /// <summary>
    /// The path to the item in the source array that will be used to create the joined data
    /// </summary>
    public required string ItemPath { get; set; }
}

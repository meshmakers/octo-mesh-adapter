using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("ApplyChanges", 2)]
public record ApplyChangesNodeConfiguration2 : NodeConfiguration
{

    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
    /// <summary>
    /// The path to the entity update
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string? EntityUpdatesPath { get; init; }

    /// <summary>
    /// The path to the association update
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string? AssociationUpdatesPath { get; init; }
}
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for checking if an entity with a matching attribute value already exists
/// </summary>
[NodeName("CheckDuplicate", 1)]
public record CheckDuplicateNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// The CK type to search for duplicates
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public required RtCkId<CkTypeId> CkTypeId { get; set; }

    /// <summary>
    /// The attribute name to match against
    /// </summary>
    [PropertyGroup("Query", 0)]
    public required string AttributeName { get; set; }

    /// <summary>
    /// JSON path to the value to check for duplicates
    /// </summary>
    [PropertyGroup("Query", 1, "jsonpath")]
    public required string ValuePath { get; set; }

    /// <summary>
    /// Output path for the existing entity if a duplicate is found (optional)
    /// </summary>
    [PropertyGroup("Output", 0, "jsonpath")]
    public string? ExistingEntityPath { get; set; }
}

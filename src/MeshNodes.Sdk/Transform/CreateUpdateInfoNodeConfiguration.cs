using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for update a rt entity object
/// </summary>
[NodeName("CreateUpdateInfo", 1)]
public record CreateUpdateInfoNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Kind of update
    /// </summary>
    [PropertyGroup("Options", 0)]
    public UpdateKind? UpdateKind { get; set; }

    /// <summary>
    /// The path to the update kind
    /// </summary>
    [PropertyGroup("Options", 1, "jsonpath")]
    public string? UpdateKindPath { get; set; }

    /// <summary>
    /// The path to the RtEntityId
    /// </summary>
    [PropertyGroup("Entity", 0, "jsonpath")]
    public string? RtIdPath { get; set; }

    /// <summary>
    /// The runtime id of the object
    /// </summary>
    [PropertyGroup("Entity", 1)]
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// When true, the RtId will be generated if it is not existing when RtIdPath is set
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public bool GenerateRtId { get; set; } = false;

    /// <summary>
    /// The runtime construction kit type id (use either this or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 3, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the JSON path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 4, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Jsonpath to the timestamp property if available
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string? TimestampPath { get; set; }

    /// <summary>
    /// Updates to the RtEntity attributes
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public ICollection<AttributeUpdateConfiguration>? AttributeUpdates { get; set; }

    /// <summary>
    /// The path to the RtWellKnownName if available
    /// </summary>
    [PropertyGroup("Entity", 5, "jsonpath")]
    public string? RtWellKnownNamePath { get; set; }
}
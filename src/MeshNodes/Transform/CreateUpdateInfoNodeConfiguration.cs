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
    public UpdateKind? UpdateKind { get; set; }
    
    /// <summary>
    /// The path to the update kind
    /// </summary>
    public string? UpdateKindPath { get; set; }
    
    /// <summary>
    /// The path to the RtEntityId
    /// </summary>
    public string? RtIdPath { get; set; }

    /// <summary>
    /// The runtime id of the object
    /// </summary>
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// CkTypeId of query
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Jsonpath to the timestamp property if available
    /// </summary>
    public string? TimestampPath { get; set; }

    /// <summary>
    /// Updates to the RtEntity attributes
    /// </summary>
    public ICollection<AttributeUpdateConfiguration>? AttributeUpdates { get; set; }

    /// <summary>
    /// The path to the RtWellKnownName if available
    /// </summary>
    public string? RtWellKnownNamePath { get; set; }
}
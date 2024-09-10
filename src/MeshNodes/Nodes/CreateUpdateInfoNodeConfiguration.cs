using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Nodes;

/// <summary>
/// Configuration node object for update a rt entity object
/// </summary>
[NodeName("CreateUpdateInfo", 1)]
public class CreateUpdateInfoNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to append to the target property name
    /// </summary>
    public bool AppendToTargetPropertyName { get; set; } = true;

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
    public string? TimestampPropertyPath { get; set; }

    /// <summary>
    /// Updates to the RtEntity attributes
    /// </summary>
    public ICollection<AttributeUpdateConfiguration>? AttributeUpdates { get; set; }
}
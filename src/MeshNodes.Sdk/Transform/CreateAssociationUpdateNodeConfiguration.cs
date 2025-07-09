using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for update a rt entity object
/// </summary>
[NodeName("CreateAssociationUpdate", 1)]
public record CreateAssociationUpdateNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Kind of update
    /// </summary>
    public AssociationUpdateKind? UpdateKind { get; set; }
    
    /// <summary>
    /// The path to the update kind
    /// </summary>
    public string? UpdateKindPath { get; set; }
    
    
    /// <summary>
    /// The path to the origin RtId
    /// </summary>
    public string? OriginRtIdPath { get; set; }

    /// <summary>
    /// The origin RtId
    /// </summary>
    public OctoObjectId? OriginRtId { get; set; }
    
    /// <summary>
    /// The path to the origin CkTypeId
    /// </summary>
    public string? OriginCkTypeIdPath { get; set; }
    
    /// <summary>
    /// The origin CkTypeId
    /// </summary>
    public CkId<CkTypeId>? OriginCkTypeId { get; set; }
    
    
    /// <summary>
    /// The path to the target RtId
    /// </summary>
    public string? TargetRtIdPath { get; set; }

    /// <summary>
    /// The target RtId
    /// </summary>
    public OctoObjectId? TargetRtId { get; set; }
    
    /// <summary>
    /// The path to the target CkTypeId
    /// </summary>
    public string? TargetCkTypeIdPath { get; set; }
    
    /// <summary>
    /// The target CkTypeId
    /// </summary>
    public CkId<CkTypeId>? TargetCkTypeId { get; set; }
    
    
    /// <summary>
    /// The path to the association role id
    /// </summary>
    public string? AssociationRoleIdPath { get; set; }
    
    /// <summary>
    /// The role id of the association
    /// </summary>
    public CkId<CkAssociationRoleId>? AssociationRoleId { get; set; }
}
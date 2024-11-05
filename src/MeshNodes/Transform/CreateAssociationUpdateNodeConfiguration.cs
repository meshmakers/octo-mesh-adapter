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
    /// The path to the source RtId
    /// </summary>
    public string? SourceRtIdPath { get; set; }

    /// <summary>
    /// The source RtId
    /// </summary>
    public OctoObjectId? SourceRtId { get; set; }
    
    /// <summary>
    /// The path to the source CkTypeId
    /// </summary>
    public string? SourceCkTypeIdPath { get; set; }
    
    /// <summary>
    /// The source CkTypeId
    /// </summary>
    public CkId<CkTypeId>? SourceCkId { get; set; }
    
    
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
    public CkId<CkTypeId>? TargetCkId { get; set; }
    
    
    /// <summary>
    /// The path to the association role id
    /// </summary>
    public string? AssociationRoleIdPath { get; set; }
    
    /// <summary>
    /// The role id of the association
    /// </summary>
    public CkId<CkAssociationRoleId>? AssociationRoleId { get; set; }
}
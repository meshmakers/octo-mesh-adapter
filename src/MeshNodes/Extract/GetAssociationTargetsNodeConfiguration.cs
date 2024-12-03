using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node 
/// </summary>
[NodeName("GetAssociationTargets", 1)]
public record GetAssociationTargetsNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Kind of update
    /// </summary>
    public GraphDirectionsDto? GraphDirection { get; set; }

    /// <summary>
    /// The path to the update kind
    /// </summary>
    public string? GraphDirectionPath { get; set; }


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
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilterDto>? FieldFilters { get; set; }
}

/// <summary>
/// Defines graph directions in graph queries
/// </summary>
public enum GraphDirectionsDto
{
    /// <summary>All inbound directions (e. g. parent to child)</summary>
    Inbound = 1,

    /// <summary>All outbound directions (e. g. child to parent)</summary>
    Outbound = 2,

    /// <summary>All directions</summary>
    Any = 3,
}
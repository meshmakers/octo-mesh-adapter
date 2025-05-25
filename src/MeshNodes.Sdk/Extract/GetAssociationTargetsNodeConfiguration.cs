using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node 
/// </summary>
[NodeName("GetAssociationTargets", 1)]
public record GetAssociationTargetsNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Defines the direction of the graph traversal
    /// </summary>
    public GraphDirectionsDto? GraphDirection { get; set; }

    /// <summary>
    /// The path to the graph direction
    /// </summary>
    public string? GraphDirectionPath { get; set; }


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
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
    
    /// <summary>
    /// A list of sort orders
    /// </summary>
    public ICollection<SortOrderDto>? SortOrders { get; set; }
}
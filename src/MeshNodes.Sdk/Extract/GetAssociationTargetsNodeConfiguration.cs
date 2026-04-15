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
    [PropertyGroup("Options", 0)]
    public GraphDirectionsDto? GraphDirection { get; set; }

    /// <summary>
    /// The path to the graph direction
    /// </summary>
    [PropertyGroup("Options", 1, "jsonpath")]
    public string? GraphDirectionPath { get; set; }


    /// <summary>
    /// The path to the origin RtId
    /// </summary>
    [PropertyGroup("Entity", 0, "jsonpath")]
    public string? OriginRtIdPath { get; set; }

    /// <summary>
    /// The origin RtId
    /// </summary>
    [PropertyGroup("Entity", 1)]
    public OctoObjectId? OriginRtId { get; set; }

    /// <summary>
    /// The path to the origin CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 2, "jsonpath")]
    public string? OriginCkTypeIdPath { get; set; }

    /// <summary>
    /// The origin runtime construction kit type id
    /// </summary>
    [PropertyGroup("Entity", 3, "ckTypeSelector")]
    public RtCkId<CkTypeId>? OriginCkTypeId { get; set; }

    /// <summary>
    /// The path to the target CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 4, "jsonpath")]
    public string? TargetCkTypeIdPath { get; set; }

    /// <summary>
    /// The target runtime construction kit type id
    /// </summary>
    [PropertyGroup("Entity", 5, "ckTypeSelector")]
    public RtCkId<CkTypeId>? TargetCkTypeId { get; set; }


    /// <summary>
    /// The path to the association role id
    /// </summary>
    [PropertyGroup("Entity", 6, "jsonpath")]
    public string? AssociationRoleIdPath { get; set; }

    /// <summary>
    /// The role id of the association
    /// </summary>
    [PropertyGroup("Entity", 7)]
    public RtCkId<CkAssociationRoleId>? AssociationRoleId { get; set; }

    /// <summary>
    /// A list of field filters
    /// </summary>
    [PropertyGroup("Query", 0)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// A list of sort orders
    /// </summary>
    [PropertyGroup("Query", 1)]
    public ICollection<SortOrderDto>? SortOrders { get; set; }
}
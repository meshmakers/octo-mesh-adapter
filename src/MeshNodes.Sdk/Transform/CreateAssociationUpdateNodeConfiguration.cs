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
    [PropertyGroup("Options", 0)]
    public AssociationUpdateKind? UpdateKind { get; set; }

    /// <summary>
    /// The path to the update kind
    /// </summary>
    [PropertyGroup("Options", 1, "jsonpath")]
    public string? UpdateKindPath { get; set; }


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
    /// The path to the target RtId
    /// </summary>
    [PropertyGroup("Entity", 4, "jsonpath")]
    public string? TargetRtIdPath { get; set; }

    /// <summary>
    /// The target RtId
    /// </summary>
    [PropertyGroup("Entity", 5)]
    public OctoObjectId? TargetRtId { get; set; }

    /// <summary>
    /// The path to the target CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 6, "jsonpath")]
    public string? TargetCkTypeIdPath { get; set; }

    /// <summary>
    /// The target runtime construction kit type id
    /// </summary>
    [PropertyGroup("Entity", 7, "ckTypeSelector")]
    public RtCkId<CkTypeId>? TargetCkTypeId { get; set; }


    /// <summary>
    /// The path to the association role id
    /// </summary>
    [PropertyGroup("Entity", 8, "jsonpath")]
    public string? AssociationRoleIdPath { get; set; }

    /// <summary>
    /// The role id of the association
    /// </summary>
    [PropertyGroup("Entity", 9)]
    public RtCkId<CkAssociationRoleId>? AssociationRoleId { get; set; }
}
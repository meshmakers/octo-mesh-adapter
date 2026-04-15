using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node get rt entities by id
/// </summary>
[NodeName("GetRtEntitiesById", 1)]
public record GetRtEntitiesByIdNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Runtime construction kit type id to filter by (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the json path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Amount of items to skip
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int? Skip { get; set; }

    /// <summary>
    /// Amount of items to take
    /// </summary>
    [PropertyGroup("Query", 1)]
    public int? Take { get; set; }

    /// <summary>
    /// Gets or sets the rt ids
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public ICollection<OctoObjectId>? RtIds { get; set; }

    /// <summary>
    /// Gets or sets the json path to the rt ids
    /// </summary>
    [PropertyGroup("Entity", 3, "jsonpath")]
    public string? RtIdsPath { get; set; }

    /// <summary>
    /// A list of field filters
    /// </summary>
    [PropertyGroup("Query", 2)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
}
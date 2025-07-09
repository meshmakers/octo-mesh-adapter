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
    /// CkTypeId of query
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// Gets or sets the json path to the CkTypeId
    /// </summary>
    public string? CkTypeIdPath { get; set; }
    
    /// <summary>
    /// Amount of items to skip
    /// </summary>
    public int? Skip { get; set; }
    
    /// <summary>
    /// Amount of items to take
    /// </summary>
    public int? Take { get; set; }
    
    /// <summary>
    /// Gets or sets the rt ids
    /// </summary>
    public ICollection<OctoObjectId>? RtIds { get; set; }
    
    /// <summary>
    /// Gets or sets the json path to the rt ids
    /// </summary>
    public string? RtIdsPath { get; set; }
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
}
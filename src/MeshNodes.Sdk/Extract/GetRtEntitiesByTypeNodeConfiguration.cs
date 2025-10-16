using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node gets rt entities by type
/// </summary>
[NodeName("GetRtEntitiesByType", 1)]
public record GetRtEntitiesByTypeNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Runtime construction kit type id to filter by (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    public RtCkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// Gets or sets the JSON path to the CkTypeId
    /// </summary>
    public string? CkTypeIdPath { get; set; }
    
    /// <summary>
    /// Number of items to skip
    /// </summary>
    public int? Skip { get; set; }
    
    /// <summary>
    /// Number of items to take
    /// </summary>
    public int? Take { get; set; }
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// A list of sort orders
    /// </summary>
    public ICollection<SortOrderDto>? SortOrders { get; set; }

}
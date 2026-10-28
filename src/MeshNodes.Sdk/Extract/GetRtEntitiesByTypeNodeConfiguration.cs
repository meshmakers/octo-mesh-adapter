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
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
    /// <summary>
    /// Runtime construction kit type id to filter by (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the JSON path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Runtime construction kit interface id, e.g. <c>System/Named</c> (AB#6933; the CK element id with its
    /// version, <c>System/Named-1</c>, is accepted too). Selects the entities of
    /// <b>all</b> types that implement the interface (derived types included) in one global order, via the
    /// engine's interface query. Use it <b>instead of</b> <c>CkTypeId</c>/<c>CkTypeIdPath</c>; setting both, or
    /// neither, fails the node. With an interface <c>Take</c> is required, <c>Skip</c> defaults to 0 and
    /// <c>Skip</c> + <c>Take</c> must not exceed 1000; field filters and sort orders may only use members of the
    /// interface (and system fields); text search, aggregations and navigation are not supported.
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public string? CkInterfaceId { get; set; }

    /// <summary>
    /// Number of items to skip
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int? Skip { get; set; }

    /// <summary>
    /// Number of items to take
    /// </summary>
    [PropertyGroup("Query", 1)]
    public int? Take { get; set; }

    /// <summary>
    /// A list of field filters
    /// </summary>
    [PropertyGroup("Query", 2)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// A list of sort orders
    /// </summary>
    [PropertyGroup("Query", 3)]
    public ICollection<SortOrderDto>? SortOrders { get; set; }

}
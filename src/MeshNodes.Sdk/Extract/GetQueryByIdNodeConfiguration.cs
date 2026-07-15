using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node get query by id
/// </summary>
[NodeName("GetQueryById", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetQueryByIdNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Gets or sets the query rt id
    /// </summary>
    [PropertyGroup("Entity", 0)]
    public required OctoObjectId QueryRtId { get; init; }

    /// <summary>
    /// Number of rows to skip
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int? Skip { get; init; }

    /// <summary>
    /// Number of rows to take
    /// </summary>
    [PropertyGroup("Query", 1)]
    public int? Take { get; init; }

    /// <summary>
    /// A list of field filters
    /// </summary>
    [PropertyGroup("Query", 2)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// Optional start of the time range (UTC), only applied to stream-data queries. When set it
    /// overrides the value persisted on the query entity; otherwise the persisted value is used.
    /// </summary>
    [PropertyGroup("StreamData", 0)]
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional end of the time range (UTC), only applied to stream-data queries. When set it
    /// overrides the value persisted on the query entity; otherwise the persisted value is used.
    /// </summary>
    [PropertyGroup("StreamData", 1)]
    public DateTime? To { get; init; }

    /// <summary>
    /// Optional row cap, only applied to stream-data queries. When set it overrides the limit
    /// persisted on the query entity; otherwise the persisted value is used.
    /// </summary>
    [PropertyGroup("StreamData", 2)]
    public int? Limit { get; init; }
}
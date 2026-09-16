using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
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
    /// A value written without a time-zone offset ("2026-06-01T00:00:00") is read as UTC, not as the
    /// adapter host's local time.
    /// </summary>
    [PropertyGroup("StreamData", 0)]
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional end of the time range (UTC), only applied to stream-data queries. When set it
    /// overrides the value persisted on the query entity; otherwise the persisted value is used.
    /// A value written without a time-zone offset ("2026-06-02T00:00:00") is read as UTC, not as the
    /// adapter host's local time.
    /// </summary>
    [PropertyGroup("StreamData", 1)]
    public DateTime? To { get; init; }

    /// <summary>
    /// Optional row cap, only applied to stream-data queries. When set it overrides the limit
    /// persisted on the query entity; otherwise the persisted value is used. For a downsampling
    /// query this is the number of time buckets rather than a row cap, and it doubles as the target
    /// point count of the archive selection.
    /// </summary>
    [PropertyGroup("StreamData", 2)]
    public int? Limit { get; init; }

    /// <summary>
    /// Optional JSONPath to read the start of the time range from the pipeline data, for cases where
    /// the range is computed upstream (HTTP trigger, previous node) instead of being configured.
    /// Only applied to stream-data queries. Precedence: <see cref="From" /> (literal) wins over this
    /// path, which wins over the value persisted on the query entity. The value may be an ISO-8601
    /// timestamp string or a date/time value; values without an offset are read as UTC. When the path
    /// resolves to nothing the persisted value is used and a warning is logged.
    /// </summary>
    [PropertyGroup("StreamData", 3)]
    public string? FromPath { get; init; }

    /// <summary>
    /// Optional JSONPath to read the end of the time range from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="To" /> takes precedence over it.
    /// </summary>
    [PropertyGroup("StreamData", 4)]
    public string? ToPath { get; init; }

    /// <summary>
    /// Optional JSONPath to read the row cap from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="Limit" /> takes precedence over it.
    /// </summary>
    [PropertyGroup("StreamData", 5)]
    public string? LimitPath { get; init; }

    /// <summary>
    /// Optional aggregation override for a downsampling stream-data query. When set it replaces the
    /// aggregation type persisted on <em>every</em> column of the query — the same set the query
    /// definition offers (Count, Minimum, Maximum, Average, Sum) — and is also the function the
    /// archive selection matches a rollup against. Not set means each column keeps its persisted
    /// aggregation and the first column's aggregation drives the archive selection.
    /// </summary>
    [PropertyGroup("StreamData", 6)]
    public AggregationTypesDto? Aggregation { get; init; }
}
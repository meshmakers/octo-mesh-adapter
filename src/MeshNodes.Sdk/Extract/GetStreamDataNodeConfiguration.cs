using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for the node that reads rows from a stream data archive. Unlike
/// <c>GetQueryById@1</c>, which executes a query entity persisted in the runtime model, everything is
/// configured directly on this node.
/// </summary>
[NodeName("GetStreamData", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetStreamDataNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the archive to read from. The archive must be activated.
    /// </summary>
    [PropertyGroup("Archive", 0)]
    public required OctoObjectId ArchiveRtId { get; init; }

    /// <summary>
    /// Attribute paths to project, as configured on the archive (e.g. "Temperature", "Amount.Value").
    /// Leave empty to read the whole archive: every one of its data columns, preceded by the
    /// well-known name of the source entity. Formula (computed) columns are only read when named
    /// here explicitly. Standard columns such as "rtWellKnownName" can be listed like any other.
    /// </summary>
    [PropertyGroup("Query", 0)]
    public ICollection<string>? Columns { get; init; }

    /// <summary>
    /// Restricts the result to rows whose source entity carries one of these well-known names. A
    /// single name is matched with Equals, several with In.
    /// </summary>
    [PropertyGroup("Query", 1)]
    public ICollection<string>? WellKnownNames { get; init; }

    /// <summary>
    /// Optional JSONPath to read the well-known names from the pipeline data instead of configuring
    /// them. Accepts a single value, an array, or a multi-match path. <see cref="WellKnownNames" />
    /// takes precedence; a path resolving to nothing leaves the filter unset and logs a warning.
    /// </summary>
    [PropertyGroup("Query", 2, "jsonpath")]
    public string? WellKnownNamesPath { get; init; }

    /// <summary>
    /// Restricts the result to these source entities. Runtime ids as strings — the same form the
    /// pipeline data carries them in.
    /// </summary>
    [PropertyGroup("Query", 3)]
    public ICollection<string>? RtIds { get; init; }

    /// <summary>
    /// Optional JSONPath to read the source entity ids from the pipeline data. Same semantics as
    /// <see cref="WellKnownNamesPath" />; <see cref="RtIds" /> takes precedence.
    /// </summary>
    [PropertyGroup("Query", 4, "jsonpath")]
    public string? RtIdsPath { get; init; }

    /// <summary>
    /// Additional filters on projected or standard columns, combined with AND.
    /// </summary>
    [PropertyGroup("Query", 5)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// Sort order of the returned rows. Use the column names as they appear in the result:
    /// "Timestamp" for the time axis (the window end on a time-range or rollup archive),
    /// "WindowStart" / "WindowEnd" on those archives, "WellKnownName", or any of the archive's own
    /// columns. An unknown name fails the node rather than returning unordered rows.
    /// </summary>
    [PropertyGroup("Query", 6)]
    public ICollection<SortOrderDto>? SortOrders { get; set; }

    /// <summary>
    /// Number of rows to skip.
    /// </summary>
    [PropertyGroup("Query", 7)]
    public int? Skip { get; init; }

    /// <summary>
    /// Number of rows to take.
    /// </summary>
    [PropertyGroup("Query", 8)]
    public int? Take { get; init; }

    /// <summary>
    /// Optional start of the time range (UTC). Leaving it unset leaves the range open at the start.
    /// A value written without a time-zone offset ("2026-07-01T00:00:00") is read as UTC, not as the
    /// adapter host's local time.
    /// </summary>
    [PropertyGroup("TimeRange", 0)]
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional JSONPath to read the start of the time range from the pipeline data, for cases where
    /// the range is computed upstream (HTTP trigger, previous node) instead of being configured.
    /// <see cref="From" /> takes precedence. The value may be an ISO-8601 timestamp string or a
    /// date/time value; values without an offset are read as UTC. A path resolving to nothing leaves
    /// the boundary open and logs a warning, while a present but non-date value fails the node.
    /// </summary>
    [PropertyGroup("TimeRange", 1, "jsonpath")]
    public string? FromPath { get; init; }

    /// <summary>
    /// Optional end of the time range (UTC). Leaving it unset leaves the range open at the end. Same
    /// UTC contract as <see cref="From" />.
    /// </summary>
    [PropertyGroup("TimeRange", 2)]
    public DateTime? To { get; init; }

    /// <summary>
    /// Optional JSONPath to read the end of the time range from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="To" /> takes precedence.
    /// </summary>
    [PropertyGroup("TimeRange", 3, "jsonpath")]
    public string? ToPath { get; init; }

    /// <summary>
    /// Optional cap on the number of rows read. Must be greater than zero when set. Independent of
    /// <see cref="Skip" /> / <see cref="Take" />, which page the result.
    /// </summary>
    [PropertyGroup("TimeRange", 4)]
    public int? Limit { get; init; }

    /// <summary>
    /// Optional JSONPath to read the row cap from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="Limit" /> takes precedence.
    /// </summary>
    [PropertyGroup("TimeRange", 5, "jsonpath")]
    public string? LimitPath { get; init; }

    /// <summary>
    /// Where to write the coverage report. Setting it turns on gap detection: the node checks
    /// whether every source entity delivered data for the whole time range and reports the
    /// uncovered ranges per entity. Requires a time range with both boundaries and an archive that
    /// stores row windows (time-range or rollup); raw archives have no windows to check.
    /// </summary>
    [PropertyGroup("Gaps", 0, "jsonpath")]
    public string? GapsTargetPath { get; init; }

    /// <summary>
    /// The interval the gap counts are expressed in, e.g. "00:15:00" for quarter-hourly data.
    /// Defaults to the period declared on the archive. Without either, gaps are still reported as
    /// time ranges but the interval counts stay empty.
    /// </summary>
    [PropertyGroup("Gaps", 1)]
    public TimeSpan? ExpectedInterval { get; init; }

    /// <summary>
    /// Report the gaps only and skip reading the data itself. Useful as a cheap completeness check
    /// ahead of an expensive step. Requires <see cref="GapsTargetPath" />.
    /// </summary>
    [PropertyGroup("Gaps", 2)]
    public bool GapsOnly { get; init; }

    /// <summary>
    /// Safety cap on the rows the gap scan reads (default 200000). Exceeding it fails the node
    /// rather than returning a report built from a truncated scan. For scale: a year of
    /// quarter-hourly data is about 35000 rows per entity.
    /// </summary>
    [PropertyGroup("Gaps", 3)]
    public int? MaxGapScanRows { get; init; }
}

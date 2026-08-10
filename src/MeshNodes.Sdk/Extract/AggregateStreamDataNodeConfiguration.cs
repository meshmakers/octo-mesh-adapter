using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for the node that condenses archive columns into key figures over a time range —
/// the sum of a month's energy, the maximum data quality in that month. Sibling of
/// <c>GetStreamData@1</c>, which returns the rows themselves.
/// </summary>
[NodeName("AggregateStreamData", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record AggregateStreamDataNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the archive to read from. The archive must be activated.
    /// </summary>
    [PropertyGroup("Archive", 0)]
    public required OctoObjectId ArchiveRtId { get; init; }

    /// <summary>
    /// The key figures to compute — at least one. The same column may appear several times with
    /// different functions.
    /// </summary>
    [PropertyGroup("Aggregation", 0)]
    public required ICollection<AggregationColumnDto> Aggregations { get; init; }

    /// <summary>
    /// Columns to group by, e.g. "rtId" for one row per source entity. Leave empty for a single row
    /// covering everything the filters select. Names follow the result vocabulary, like
    /// <see cref="FieldFilters" />.
    /// </summary>
    [PropertyGroup("Aggregation", 1)]
    public ICollection<string>? GroupBy { get; init; }

    /// <summary>
    /// Restricts the aggregation to rows whose source entity carries one of these well-known names.
    /// A single name is matched with Equals, several with In.
    /// </summary>
    [PropertyGroup("Query", 0)]
    public ICollection<string>? WellKnownNames { get; init; }

    /// <summary>
    /// Optional JSONPath to read the well-known names from the pipeline data instead of configuring
    /// them. Accepts a single value, an array, or a multi-match path. <see cref="WellKnownNames" />
    /// takes precedence; a path resolving to nothing leaves the filter unset and logs a warning.
    /// </summary>
    [PropertyGroup("Query", 1, "jsonpath")]
    public string? WellKnownNamesPath { get; init; }

    /// <summary>
    /// Restricts the aggregation to these source entities. Runtime ids as strings — the same form
    /// the pipeline data carries them in.
    /// </summary>
    [PropertyGroup("Query", 2)]
    public ICollection<string>? RtIds { get; init; }

    /// <summary>
    /// Optional JSONPath to read the source entity ids from the pipeline data. Same semantics as
    /// <see cref="WellKnownNamesPath" />; <see cref="RtIds" /> takes precedence.
    /// </summary>
    [PropertyGroup("Query", 3, "jsonpath")]
    public string? RtIdsPath { get; init; }

    /// <summary>
    /// Additional filters on the archive's columns, combined with AND. Names follow the result
    /// vocabulary: "Timestamp", "WindowStart" / "WindowEnd" on time-range and rollup archives,
    /// "WellKnownName", or any column the archive declares. An unknown name fails the node rather
    /// than being ignored — an ignored filter would widen the result and inflate the figures.
    /// </summary>
    [PropertyGroup("Query", 4)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// Start of the aggregated time range (UTC). Leaving it unset aggregates from the beginning of
    /// the archive. A value written without a time-zone offset ("2026-07-01T00:00:00") is read as
    /// UTC, not as the adapter host's local time.
    /// </summary>
    [PropertyGroup("TimeRange", 0)]
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional JSONPath to read the start of the range from the pipeline data, for ranges computed
    /// upstream instead of configured. <see cref="From" /> takes precedence. A path resolving to
    /// nothing leaves the boundary open and logs a warning; a present but non-date value fails the
    /// node.
    /// </summary>
    [PropertyGroup("TimeRange", 1, "jsonpath")]
    public string? FromPath { get; init; }

    /// <summary>
    /// End of the aggregated time range (UTC). Leaving it unset aggregates to the end of the
    /// archive. Same UTC contract as <see cref="From" />.
    /// </summary>
    [PropertyGroup("TimeRange", 2)]
    public DateTime? To { get; init; }

    /// <summary>
    /// Optional JSONPath to read the end of the range from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="To" /> takes precedence.
    /// </summary>
    [PropertyGroup("TimeRange", 3, "jsonpath")]
    public string? ToPath { get; init; }

    /// <summary>
    /// Only aggregate when every source entity delivered data for the whole time range. An
    /// incomplete range would otherwise produce a figure that looks valid but is too low — a month
    /// missing two days still returns a sum. Requires a time range with both boundaries and an
    /// archive that stores row windows (time-range or rollup).
    /// </summary>
    [PropertyGroup("Gaps", 0)]
    public bool RequireGapFree { get; init; }

    /// <summary>
    /// The interval the completeness check counts in, e.g. "00:15:00" for quarter-hourly data. Must
    /// be greater than zero when set. Defaults to the period declared on the archive. Only used
    /// together with <see cref="RequireGapFree" />.
    /// </summary>
    [PropertyGroup("Gaps", 1)]
    public TimeSpan? ExpectedInterval { get; init; }

    /// <summary>
    /// Safety cap on the rows the completeness check reads (default 200000). Exceeding it fails the
    /// node rather than judging completeness from a truncated scan. Must be greater than zero when
    /// set — use the largest possible integer to scan without a practical cap, not zero.
    /// </summary>
    [PropertyGroup("Gaps", 2)]
    public int? MaxGapScanRows { get; init; }
}

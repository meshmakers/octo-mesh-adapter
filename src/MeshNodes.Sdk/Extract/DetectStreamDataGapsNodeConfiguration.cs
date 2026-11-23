using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for the node that finds the missing time ranges of many series in one windowed
/// stream data archive (time-range or rollup archive). For every series it reports the ranges
/// [start, end) in which the archive holds no data and the number of missing
/// <see cref="ExpectedInterval" /> slots, over the half-open period [<see cref="From" />,
/// <see cref="To" />). It reads only the configured archive and has no row limit: it batches and
/// pages internally, however many series and rows the period holds.
/// </summary>
[NodeName("DetectStreamDataGaps", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record DetectStreamDataGapsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the archive to check. It must be activated and store row windows (a
    /// time-range or rollup archive); a raw archive holding single timestamps fails the node.
    /// </summary>
    [PropertyGroup("Archive", 0)]
    public required OctoObjectId ArchiveRtId { get; init; }

    /// <summary>
    /// The series to check, as runtime ids of their source entities. A series without any data in
    /// the period is reported as one missing range over the whole period, so name every series you
    /// expect data for.
    /// </summary>
    [PropertyGroup("Series", 0)]
    public ICollection<string>? RtIds { get; init; }

    /// <summary>
    /// Optional JSONPath to read the series runtime ids from the pipeline data (a single value, an
    /// array, or a multi-match path). <see cref="RtIds" /> takes precedence. A path resolving to
    /// nothing checks no series and writes an empty result with a warning.
    /// </summary>
    [PropertyGroup("Series", 1, "jsonpath")]
    public string? RtIdsPath { get; init; }

    /// <summary>
    /// Start of the period, inclusive. Give it with its UTC offset
    /// ("2026-07-01T00:00:00+02:00"): the offset also decides where a day starts, so a calendar
    /// month in local time is from local midnight of the first day to local midnight of the next
    /// month. A value without an offset is read as UTC.
    /// </summary>
    [PropertyGroup("Period", 0)]
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional JSONPath to read the start of the period from the pipeline data (an ISO-8601
    /// timestamp with offset, or a date/time value). <see cref="From" /> takes precedence.
    /// </summary>
    [PropertyGroup("Period", 1, "jsonpath")]
    public string? FromPath { get; init; }

    /// <summary>
    /// End of the period, exclusive. Same offset rules as <see cref="From" />; it must be later
    /// than <see cref="From" />.
    /// </summary>
    [PropertyGroup("Period", 2)]
    public DateTime? To { get; init; }

    /// <summary>
    /// Optional JSONPath to read the end of the period from the pipeline data. Same semantics as
    /// <see cref="FromPath" />; <see cref="To" /> takes precedence.
    /// </summary>
    [PropertyGroup("Period", 3, "jsonpath")]
    public string? ToPath { get; init; }

    /// <summary>
    /// The length of one expected value slot, e.g. "00:15:00" for quarter-hourly data or "01:00:00"
    /// for hourly data. The missing counts are expressed in these slots. Defaults to the period
    /// declared on the archive; must be greater than zero.
    /// </summary>
    [PropertyGroup("Period", 4)]
    public TimeSpan? ExpectedInterval { get; init; }

    /// <summary>
    /// Filters on archive or standard columns, combined with AND (e.g. Amount.Unit equals 1). Only
    /// rows that match every filter count as present.
    /// </summary>
    [PropertyGroup("Filter", 0)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// Optional validity periods per series, each the half-open range [from, to) with
    /// <c>to</c> unset for an open end. When set, only the slots inside [<see cref="From" />,
    /// <see cref="To" />) AND inside one of a series' periods are checked: missing ranges are cut
    /// exactly at the validity boundaries, a gap across a hole between two periods becomes two ranges,
    /// and the counts cover only those slots. A series with several periods lists one entry per
    /// period; overlapping or touching periods of one series are merged. A series without any entry
    /// is not checked and not reported (one info line names how many). Stretches outside every
    /// period are not read. An entry with from &gt;= to fails the node naming the entry.
    /// </summary>
    [PropertyGroup("Validity", 0)]
    public ICollection<ValidityPeriodDto>? ValidityPeriods { get; init; }

    /// <summary>
    /// Optional JSONPath to read the validity periods from the pipeline data: an array of objects
    /// with the fields <c>rtId</c>, <c>from</c> and optionally <c>to</c> (ISO-8601 timestamps with
    /// offset), or a multi-match path of such objects. <see cref="ValidityPeriods" /> takes
    /// precedence. A path resolving to no entry reports no series and writes a warning naming the
    /// path.
    /// </summary>
    [PropertyGroup("Validity", 1, "jsonpath")]
    public string? ValidityPeriodsPath { get; init; }
}

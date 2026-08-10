using System.Text.Json.Serialization;
using System.Xml;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Coverage report for a queried time range: which source entities delivered data for the whole
/// window and where they did not. Produced by <see cref="StreamDataGapAnalyzer" /> and written to
/// the node's gaps target path.
/// </summary>
internal sealed class StreamDataGapReport
{
    /// <summary>Start of the analysed window (UTC), inclusive.</summary>
    public required DateTime From { get; init; }

    /// <summary>End of the analysed window (UTC), exclusive.</summary>
    public required DateTime To { get; init; }

    /// <summary>
    /// The interval the counts are expressed in — the configured one, else the archive's declared
    /// period. Null when neither is known; the gaps are still reported as time ranges, only the
    /// interval counts stay null.
    /// </summary>
    public string? Interval { get; init; }

    /// <summary>Number of source entities the report covers.</summary>
    public required int SeriesCount { get; init; }

    /// <summary>Number of those entities that have at least one gap.</summary>
    public required int SeriesWithGapsCount { get; init; }

    /// <summary>True when no series has a gap.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>One entry per source entity, ordered by well-known name.</summary>
    public required List<StreamDataGapSeries> Series { get; init; }
}

/// <summary>
/// Coverage of one source entity within the analysed window.
/// </summary>
internal sealed class StreamDataGapSeries
{
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId? RtId { get; init; }

    public string? WellKnownName { get; init; }

    /// <summary>Intervals the window should contain. Null when no interval is known.</summary>
    public int? ExpectedIntervals { get; init; }

    /// <summary>Intervals actually covered. Null when no interval is known.</summary>
    public int? PresentIntervals { get; init; }

    /// <summary>Intervals missing across all gaps. Null when no interval is known.</summary>
    public int? MissingIntervals { get; init; }

    /// <summary>Total time covered by data, as an ISO-8601 duration.</summary>
    public required string CoveredDuration { get; init; }

    public required double CoveredDurationSeconds { get; init; }

    /// <summary>Total time not covered, as an ISO-8601 duration.</summary>
    public required string MissingDuration { get; init; }

    public required double MissingDurationSeconds { get; init; }

    /// <summary>
    /// True when two of this entity's windows overlap. Not a gap and not an error — the storage
    /// concept allows overlapping windows — but a sum over them counts the overlap twice, so it is
    /// reported rather than swallowed.
    /// </summary>
    public required bool HasOverlaps { get; init; }

    /// <summary>True when the entity covers the whole window.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>The uncovered ranges, in chronological order.</summary>
    public required List<StreamDataGap> Gaps { get; init; }
}

/// <summary>
/// One uncovered range within the analysed window.
/// </summary>
internal sealed class StreamDataGap
{
    public required DateTime From { get; init; }

    public required DateTime To { get; init; }

    /// <summary>Length of the gap as an ISO-8601 duration — readable in logs and reports.</summary>
    public required string Duration { get; init; }

    /// <summary>Length of the gap in seconds — computable in downstream nodes.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// How many intervals the gap spans, rounded up. Null when no interval is known.
    /// </summary>
    public int? MissingIntervals { get; init; }
}

/// <summary>
/// Formats a <see cref="TimeSpan" /> the way the report exposes it.
/// </summary>
internal static class StreamDataGapDuration
{
    internal static string ToIso8601(TimeSpan value) => XmlConvert.ToString(value);
}

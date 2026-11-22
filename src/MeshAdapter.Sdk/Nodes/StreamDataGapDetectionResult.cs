namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// What <c>DetectStreamDataGaps@1</c> writes: the missing time ranges per series of one archive
/// over [<see cref="From" />, <see cref="To" />). Series without gaps are omitted; the remaining
/// series are sorted by runtime id (ordinal), their ranges by start, so the same data always gives
/// the same output.
/// </summary>
internal sealed class StreamDataGapDetectionResult
{
    /// <summary>Start of the checked period (UTC), inclusive.</summary>
    public required DateTime From { get; init; }

    /// <summary>End of the checked period (UTC), exclusive.</summary>
    public required DateTime To { get; init; }

    /// <summary>The slot length the counts are expressed in, ISO-8601 (e.g. <c>PT15M</c>).</summary>
    public required string Interval { get; init; }

    /// <summary>Slots per series in the period.</summary>
    public required int ExpectedCount { get; init; }

    /// <summary>Number of distinct series checked.</summary>
    public required int SeriesCount { get; init; }

    /// <summary>Number of series with at least one missing range (the length of <see cref="Series" />).</summary>
    public required int SeriesWithGapsCount { get; init; }

    /// <summary>Missing slots summed over every series.</summary>
    public required long MissingCount { get; init; }

    /// <summary>The series with gaps.</summary>
    public required List<StreamDataGapDetectionSeries> Series { get; init; }
}

/// <summary>One series with its missing ranges.</summary>
internal sealed class StreamDataGapDetectionSeries
{
    /// <summary>Runtime id of the series' source entity.</summary>
    public required string RtId { get; init; }

    /// <summary>Missing slots of this series in the period.</summary>
    public required int MissingCount { get; init; }

    /// <summary>
    /// True when two windows of this series overlap in the period. Overlaps never produce a
    /// negative or double count; the flag only tells that the archive holds them.
    /// </summary>
    public required bool HasOverlaps { get; init; }

    /// <summary>The missing ranges, sorted by start; touching ranges are merged into one.</summary>
    public required List<StreamDataGapDetectionRange> Ranges { get; init; }
}

/// <summary>One missing range [<see cref="Start" />, <see cref="End" />).</summary>
internal sealed class StreamDataGapDetectionRange
{
    /// <summary>Start of the missing range (UTC), inclusive.</summary>
    public required DateTime Start { get; init; }

    /// <summary>End of the missing range (UTC), exclusive.</summary>
    public required DateTime End { get; init; }

    /// <summary>Missing slots in the range; a range shorter than one slot counts as one.</summary>
    public required int MissingCount { get; init; }
}

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Finds the uncovered ranges of a queried window, per source entity, from the row windows a
/// windowed archive returned. Pure and storage-free so the whole rule set is testable without a
/// database.
/// <para>
/// Coverage/union rather than a fixed grid: every <c>[window_start, window_end)</c> is clamped to
/// the queried range, overlapping and adjacent ones are merged, and whatever the merged ranges do
/// not cover is a gap. That needs no declared period — a <c>TimeRangeArchive</c>'s <c>Period</c> is
/// advisory and may be null — and it copes with windows of differing length. A known interval only
/// adds the counts on top.
/// </para>
/// </summary>
internal static class StreamDataGapAnalyzer
{
    /// <summary>
    /// One row's window, already extracted from the storage result.
    /// </summary>
    internal readonly record struct Window(DateTime Start, DateTime End);

    /// <summary>
    /// One source entity's windows within the queried range.
    /// </summary>
    internal sealed class Series
    {
        public OctoObjectId? RtId { get; init; }
        public string? WellKnownName { get; init; }
        public required List<Window> Windows { get; init; }
    }

    /// <summary>
    /// Groups the rows by source entity and extracts each row's window. A row missing either
    /// boundary is skipped — it cannot say anything about coverage.
    /// <para>
    /// <c>expectedEntities</c> are the entities the caller knows should be present. A coverage scan
    /// only sees entities that returned at least one row, so one that delivered nothing at all would
    /// otherwise be invisible; every expected entity without rows becomes a series with no windows,
    /// which the analysis then reports as a full-window gap.
    /// </para>
    /// </summary>
    internal static List<Series> BuildSeries(IEnumerable<StreamDataRow> rows,
        IEnumerable<(OctoObjectId? RtId, string? WellKnownName)>? expectedEntities = null)
    {
        var byEntity = new Dictionary<string, Series>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var start = AsDateTime(StreamDataNodeHelpers.ResolveStreamColumnValue(
                row.Values, StreamDataNodeHelpers.WindowStartColumn));
            // window_end is aliased as the time axis on a windowed archive, so it arrives as
            // Timestamp; fall back to the projected column for completeness.
            var end = row.Timestamp
                      ?? AsDateTime(StreamDataNodeHelpers.ResolveStreamColumnValue(
                          row.Values, StreamDataNodeHelpers.WindowEndColumn));

            // Register the entity even when the row carries no usable window: it did deliver
            // something, and surfacing it as a fully uncovered series is more honest than dropping
            // it from the report altogether.
            var series = GetOrAdd(byEntity, row.RtId, row.RtWellKnownName);

            if (start is null || end is null)
            {
                continue;
            }

            series.Windows.Add(new Window(ToUtc(start.Value), ToUtc(end.Value)));
        }

        if (expectedEntities != null)
        {
            foreach (var (rtId, wellKnownName) in expectedEntities)
            {
                GetOrAdd(byEntity, rtId, wellKnownName);
            }
        }

        return byEntity.Values
            .OrderBy(s => s.WellKnownName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RtId?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Analyses the coverage of the range from..to for every series.
    /// <para>
    /// <c>interval</c> is what the counts are expressed in, or null when neither the node nor the
    /// archive declares one — the gaps are then reported as plain time ranges without counts.
    /// </para>
    /// </summary>
    internal static StreamDataGapReport Analyse(IReadOnlyList<Series> series, DateTime from,
        DateTime to, TimeSpan? interval)
    {
        var results = series.Select(s => AnalyseSeries(s, from, to, interval)).ToList();

        return new StreamDataGapReport
        {
            From = from,
            To = to,
            Interval = interval.HasValue ? StreamDataGapDuration.ToIso8601(interval.Value) : null,
            SeriesCount = results.Count,
            SeriesWithGapsCount = results.Count(r => r.Gaps.Count != 0),
            IsComplete = results.All(r => r.IsComplete),
            Series = results
        };
    }

    private static StreamDataGapSeries AnalyseSeries(Series series, DateTime from, DateTime to,
        TimeSpan? interval)
    {
        var (covered, hasOverlaps) = MergeClampedWindows(series.Windows, from, to);

        var gaps = new List<StreamDataGap>();
        var cursor = from;

        foreach (var range in covered)
        {
            if (range.Start > cursor)
            {
                gaps.Add(BuildGap(cursor, range.Start, interval));
            }

            if (range.End > cursor)
            {
                cursor = range.End;
            }
        }

        if (cursor < to)
        {
            gaps.Add(BuildGap(cursor, to, interval));
        }

        var coveredDuration = covered.Aggregate(TimeSpan.Zero, (sum, r) => sum + (r.End - r.Start));
        var missingDuration = to - from - coveredDuration;

        int? expectedIntervals = null;
        int? missingIntervals = null;
        int? presentIntervals = null;
        if (interval is { Ticks: > 0 })
        {
            expectedIntervals = CountIntervals(to - from, interval.Value);
            missingIntervals = gaps.Sum(g => g.MissingIntervals ?? 0);
            presentIntervals = Math.Max(0, expectedIntervals.Value - missingIntervals.Value);
        }

        return new StreamDataGapSeries
        {
            RtId = series.RtId,
            WellKnownName = series.WellKnownName,
            ExpectedIntervals = expectedIntervals,
            PresentIntervals = presentIntervals,
            MissingIntervals = missingIntervals,
            CoveredDuration = StreamDataGapDuration.ToIso8601(coveredDuration),
            CoveredDurationSeconds = coveredDuration.TotalSeconds,
            MissingDuration = StreamDataGapDuration.ToIso8601(missingDuration),
            MissingDurationSeconds = missingDuration.TotalSeconds,
            HasOverlaps = hasOverlaps,
            IsComplete = gaps.Count == 0,
            Gaps = gaps
        };
    }

    /// <summary>
    /// Clamps every window to the queried range, drops the ones that fall outside it entirely, and
    /// merges what overlaps or touches. Reports whether any two windows genuinely overlapped —
    /// touching end-to-start does not count, that is just a continuous series.
    /// </summary>
    private static (List<Window> Covered, bool HasOverlaps) MergeClampedWindows(
        IEnumerable<Window> windows, DateTime from, DateTime to)
    {
        var clamped = windows
            .Select(w => new Window(w.Start < from ? from : w.Start, w.End > to ? to : w.End))
            .Where(w => w.End > w.Start)
            .OrderBy(w => w.Start)
            .ThenBy(w => w.End)
            .ToList();

        var covered = new List<Window>();
        var hasOverlaps = false;

        foreach (var window in clamped)
        {
            if (covered.Count == 0)
            {
                covered.Add(window);
                continue;
            }

            var last = covered[^1];
            if (window.Start > last.End)
            {
                covered.Add(window);
                continue;
            }

            // Starts before the previous one ended: a real overlap, not just a shared boundary.
            if (window.Start < last.End)
            {
                hasOverlaps = true;
            }

            if (window.End > last.End)
            {
                covered[^1] = new Window(last.Start, window.End);
            }
        }

        return (covered, hasOverlaps);
    }

    private static StreamDataGap BuildGap(DateTime from, DateTime to, TimeSpan? interval)
    {
        var duration = to - from;

        return new StreamDataGap
        {
            From = from,
            To = to,
            Duration = StreamDataGapDuration.ToIso8601(duration),
            DurationSeconds = duration.TotalSeconds,
            MissingIntervals = interval is { Ticks: > 0 } ? CountIntervals(duration, interval.Value) : null
        };
    }

    /// <summary>
    /// Intervals a duration spans, rounded up: a gap shorter than one interval still means one
    /// interval was not delivered in full.
    /// </summary>
    private static int CountIntervals(TimeSpan duration, TimeSpan interval)
        => (int)Math.Ceiling((double)duration.Ticks / interval.Ticks);

    private static Series GetOrAdd(IDictionary<string, Series> byEntity, OctoObjectId? rtId,
        string? wellKnownName)
    {
        // Group by rtId where present; an archive row without one still gets its own bucket keyed by
        // the well-known name rather than being merged into an unrelated series.
        var key = rtId?.ToString() ?? $"wkn:{wellKnownName}";
        if (byEntity.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var series = new Series { RtId = rtId, WellKnownName = wellKnownName, Windows = [] };
        byEntity[key] = series;
        return series;
    }

    private static DateTime? AsDateTime(object? value) => value switch
    {
        DateTime dateTime => dateTime,
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
        _ => null
    };

    private static DateTime ToUtc(DateTime value) => StreamDataNodeHelpers.ToUtc(value);
}

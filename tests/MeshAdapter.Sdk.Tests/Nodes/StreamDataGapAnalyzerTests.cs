using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Tests for the coverage/union gap analysis. Storage-free by design, so every rule is exercised
/// here rather than against a database.
/// </summary>
public class StreamDataGapAnalyzerTests
{
    private static readonly DateTime From = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly OctoObjectId MeterA = new("000000000000000000000001");
    private static readonly OctoObjectId MeterB = new("000000000000000000000002");

    /// <summary>Minutes past <see cref="From" />.</summary>
    private static DateTime At(int minutes) => From.AddMinutes(minutes);

    private static StreamDataGapAnalyzer.Series SeriesOf(params (int StartMin, int EndMin)[] windows)
        => new()
        {
            RtId = MeterA,
            WellKnownName = "METER-A",
            Windows = windows
                .Select(w => new StreamDataGapAnalyzer.Window(At(w.StartMin), At(w.EndMin)))
                .ToList()
        };

    /// <summary>
    /// Analyses one series over the standard window. Defaults to the quarter-hour interval; pass
    /// <c>withInterval: false</c> for the "no interval known" case.
    /// </summary>
    private static StreamDataGapSeries Analyse(StreamDataGapAnalyzer.Series series,
        bool withInterval = true)
        => Assert.Single(
            StreamDataGapAnalyzer.Analyse([series], From, To, withInterval ? Quarter : null).Series);

    // ------------------------------------------------------------------ coverage

    [Fact]
    public void Analyse_FullyCovered_ReportsNoGap()
    {
        var result = Analyse(SeriesOf((0, 15), (15, 30), (30, 45), (45, 60)));

        Assert.True(result.IsComplete);
        Assert.Empty(result.Gaps);
        Assert.Equal(4, result.ExpectedIntervals);
        Assert.Equal(4, result.PresentIntervals);
        Assert.Equal(0, result.MissingIntervals);
        Assert.Equal(0, result.MissingDurationSeconds);
    }

    [Fact]
    public void Analyse_GapInTheMiddle_IsReported()
    {
        // 12:30..12:45 missing — the concept's worked example.
        var result = Analyse(SeriesOf((0, 15), (15, 30), (45, 60)));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(At(30), gap.From);
        Assert.Equal(At(45), gap.To);
        Assert.Equal(1, gap.MissingIntervals);
        Assert.Equal("PT15M", gap.Duration);
        Assert.Equal(900, gap.DurationSeconds);
        Assert.False(result.IsComplete);
        Assert.Equal(3, result.PresentIntervals);
    }

    [Fact]
    public void Analyse_GapAtTheStart_IsReported()
    {
        var result = Analyse(SeriesOf((15, 30), (30, 45), (45, 60)));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(From, gap.From);
        Assert.Equal(At(15), gap.To);
    }

    [Fact]
    public void Analyse_GapAtTheEnd_IsReported()
    {
        var result = Analyse(SeriesOf((0, 15), (15, 30)));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(At(30), gap.From);
        Assert.Equal(To, gap.To);
        Assert.Equal(2, gap.MissingIntervals);
    }

    [Fact]
    public void Analyse_SeveralGaps_AreAllReported()
    {
        var result = Analyse(SeriesOf((0, 15), (30, 45)));

        Assert.Equal(2, result.Gaps.Count);
        Assert.Equal((At(15), At(30)), (result.Gaps[0].From, result.Gaps[0].To));
        Assert.Equal((At(45), To), (result.Gaps[1].From, result.Gaps[1].To));
        Assert.Equal(2, result.MissingIntervals);
    }

    [Fact]
    public void Analyse_NoWindowsAtAll_ReportsTheWholeRange()
    {
        var result = Analyse(SeriesOf());

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(From, gap.From);
        Assert.Equal(To, gap.To);
        Assert.Equal(4, gap.MissingIntervals);
        Assert.Equal(0, result.PresentIntervals);
        Assert.False(result.IsComplete);
    }

    // ----------------------------------------------------------------- merging

    [Fact]
    public void Analyse_AdjacentWindows_AreNotAnOverlap()
    {
        // Touching end-to-start is a continuous series, not a double delivery.
        var result = Analyse(SeriesOf((0, 15), (15, 30), (30, 45), (45, 60)));

        Assert.False(result.HasOverlaps);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Analyse_OverlappingWindows_AreMergedAndFlagged()
    {
        // A one-hour window plus a half-hour window inside it: covered once, overlap reported.
        var result = Analyse(SeriesOf((0, 60), (0, 30)));

        Assert.True(result.HasOverlaps);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Gaps);
        // Covered duration counts the union, not the sum of the windows.
        Assert.Equal(3600, result.CoveredDurationSeconds);
    }

    [Fact]
    public void Analyse_UnorderedWindows_AreSortedBeforeMerging()
    {
        var result = Analyse(SeriesOf((45, 60), (0, 15), (30, 45), (15, 30)));

        Assert.True(result.IsComplete);
        Assert.False(result.HasOverlaps);
    }

    [Fact]
    public void Analyse_WindowsOutsideTheRange_AreClamped()
    {
        // Reaches an hour before and after; only the queried hour counts.
        var result = Analyse(SeriesOf((-60, 120)));

        Assert.True(result.IsComplete);
        Assert.Equal(3600, result.CoveredDurationSeconds);
        Assert.Equal(4, result.PresentIntervals);
    }

    [Fact]
    public void Analyse_WindowEntirelyOutsideTheRange_IsIgnored()
    {
        var result = Analyse(SeriesOf((-120, -60)));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(From, gap.From);
        Assert.Equal(To, gap.To);
    }

    [Fact]
    public void Analyse_VariableWindowLengths_AreHandled()
    {
        // No fixed grid: a 20-minute and a 40-minute window cover the hour exactly.
        var result = Analyse(SeriesOf((0, 20), (20, 60)), withInterval: false);

        Assert.True(result.IsComplete);
        Assert.Equal(3600, result.CoveredDurationSeconds);
    }

    // ---------------------------------------------------------------- interval

    [Fact]
    public void Analyse_WithoutInterval_ReportsRangesButNoCounts()
    {
        var result = Analyse(SeriesOf((0, 15), (45, 60)), withInterval: false);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(At(15), gap.From);
        Assert.Equal(At(45), gap.To);
        Assert.Null(gap.MissingIntervals);
        Assert.Null(result.ExpectedIntervals);
        Assert.Null(result.PresentIntervals);
        Assert.Null(result.MissingIntervals);
        // The duration is exact even without an interval.
        Assert.Equal(1800, gap.DurationSeconds);
    }

    [Fact]
    public void Analyse_PartialInterval_RoundsMissingCountUp()
    {
        // A five-minute hole still means one quarter-hour was not delivered in full.
        var result = Analyse(SeriesOf((0, 15), (20, 60)));

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(300, gap.DurationSeconds);
        Assert.Equal(1, gap.MissingIntervals);
    }

    [Fact]
    public void Analyse_ReportsIntervalAsIso8601()
    {
        var report = StreamDataGapAnalyzer.Analyse([SeriesOf((0, 60))], From, To, Quarter);

        Assert.Equal("PT15M", report.Interval);
    }

    // ------------------------------------------------------------ several series

    [Fact]
    public void Analyse_SeriesAreIndependent()
    {
        var complete = new StreamDataGapAnalyzer.Series
        {
            RtId = MeterA,
            WellKnownName = "METER-A",
            Windows = [new StreamDataGapAnalyzer.Window(From, To)]
        };
        var incomplete = new StreamDataGapAnalyzer.Series
        {
            RtId = MeterB,
            WellKnownName = "METER-B",
            Windows = [new StreamDataGapAnalyzer.Window(From, At(30))]
        };

        var report = StreamDataGapAnalyzer.Analyse([complete, incomplete], From, To, Quarter);

        Assert.Equal(2, report.SeriesCount);
        Assert.Equal(1, report.SeriesWithGapsCount);
        Assert.False(report.IsComplete);
        // One meter's gap must not be hidden by another meter delivering.
        Assert.True(report.Series.Single(s => s.WellKnownName == "METER-A").IsComplete);
        Assert.False(report.Series.Single(s => s.WellKnownName == "METER-B").IsComplete);
    }

    [Fact]
    public void Analyse_EmptySeriesList_IsComplete()
    {
        var report = StreamDataGapAnalyzer.Analyse([], From, To, Quarter);

        Assert.Equal(0, report.SeriesCount);
        Assert.True(report.IsComplete);
        Assert.Empty(report.Series);
    }

    // ---------------------------------------------------------------- BuildSeries

    [Fact]
    public void BuildSeries_GroupsRowsByEntity()
    {
        var rows = new[]
        {
            Row(MeterA, "METER-A", 0, 15),
            Row(MeterA, "METER-A", 15, 30),
            Row(MeterB, "METER-B", 0, 15)
        };

        var series = StreamDataGapAnalyzer.BuildSeries(rows);

        Assert.Equal(2, series.Count);
        Assert.Equal(2, series.Single(s => s.RtId == MeterA).Windows.Count);
        Assert.Single(series.Single(s => s.RtId == MeterB).Windows);
    }

    [Fact]
    public void BuildSeries_UsesRowTimestampAsWindowEnd()
    {
        // A windowed archive aliases window_end as the row timestamp.
        var series = Assert.Single(StreamDataGapAnalyzer.BuildSeries([Row(MeterA, "METER-A", 0, 15)]));

        var window = Assert.Single(series.Windows);
        Assert.Equal(From, window.Start);
        Assert.Equal(At(15), window.End);
    }

    [Fact]
    public void BuildSeries_SkipsRowsWithoutAWindowStart()
    {
        var incomplete = new StreamDataRow
        {
            RtId = MeterA,
            Timestamp = At(15),
            Values = new Dictionary<string, object?>()
        };

        // The entity still shows up — it delivered a row, just not a usable window — and the
        // analysis then reports it as fully uncovered rather than omitting it.
        var series = Assert.Single(StreamDataGapAnalyzer.BuildSeries([incomplete]));

        Assert.Empty(series.Windows);
        Assert.False(Analyse(series).IsComplete);
    }

    [Fact]
    public void BuildSeries_UnspecifiedKindWindows_AreReadAsUtc()
    {
        var row = new StreamDataRow
        {
            RtId = MeterA,
            Timestamp = DateTime.SpecifyKind(At(15), DateTimeKind.Unspecified),
            Values = new Dictionary<string, object?>
            {
                [StreamDataNodeHelpers.WindowStartColumn] =
                    DateTime.SpecifyKind(From, DateTimeKind.Unspecified)
            }
        };

        var window = Assert.Single(Assert.Single(StreamDataGapAnalyzer.BuildSeries([row])).Windows);

        Assert.Equal(DateTimeKind.Utc, window.Start.Kind);
        Assert.Equal(DateTimeKind.Utc, window.End.Kind);
        Assert.Equal(From, window.Start);
    }

    [Fact]
    public void BuildSeries_ExpectedEntityWithoutRows_BecomesAnEmptySeries()
    {
        // The known limitation's mitigation: an entity that delivered nothing is invisible to a
        // coverage scan unless the caller named it.
        var series = StreamDataGapAnalyzer.BuildSeries(
            [Row(MeterA, "METER-A", 0, 60)],
            [(MeterA, "METER-A"), (MeterB, null)]);

        Assert.Equal(2, series.Count);
        Assert.Empty(series.Single(s => s.RtId == MeterB).Windows);

        var report = StreamDataGapAnalyzer.Analyse(series, From, To, Quarter);
        Assert.Equal(1, report.SeriesWithGapsCount);
        Assert.Single(report.Series.Single(s => s.RtId == MeterB).Gaps);
    }

    [Fact]
    public void BuildSeries_ExpectedEntityWithRows_IsNotDuplicated()
    {
        var series = StreamDataGapAnalyzer.BuildSeries(
            [Row(MeterA, "METER-A", 0, 60)],
            [(MeterA, "METER-A")]);

        Assert.Single(series);
        Assert.Single(series[0].Windows);
    }

    private static StreamDataRow Row(OctoObjectId rtId, string wellKnownName, int startMin, int endMin)
        => new()
        {
            RtId = rtId,
            RtWellKnownName = wellKnownName,
            Timestamp = At(endMin),
            Values = new Dictionary<string, object?>
            {
                [StreamDataNodeHelpers.WindowStartColumn] = At(startMin)
            }
        };
}

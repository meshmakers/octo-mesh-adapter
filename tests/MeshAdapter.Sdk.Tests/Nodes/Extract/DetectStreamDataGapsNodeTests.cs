using System.Globalization;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// Unit tests for <c>DetectStreamDataGaps@1</c> against an in-memory windowed archive that answers
/// both the row read and the per-day COUNT the way the CrateDB repository does (overlap time filter,
/// paging, local-day bins with the containment rule and the straddling refusal).
/// </summary>
public class DetectStreamDataGapsNodeTests : NodeTestBase
{
    private const string TestTenantId = "test-tenant";
    private static readonly OctoObjectId TestArchiveRtId = new("000000000000000000000042");
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/Meter");
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // Ordinal order A < B < C, so sorting is checkable.
    private static readonly OctoObjectId SeriesA = new("0000000000000000000000a1");
    private static readonly OctoObjectId SeriesB = new("0000000000000000000000b2");
    private static readonly OctoObjectId SeriesC = new("0000000000000000000000c3");

    // A local calendar day in UTC+2: 2026-07-01T00:00:00+02:00.
    private static readonly DateTime Day1 = new(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly IArchiveRuntimeStore _archiveStore = A.Fake<IArchiveRuntimeStore>();
    private readonly InMemoryWindowedArchive _archive = new();

    public DetectStreamDataGapsNodeTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _systemContext.FindTenantContextAsync(TestTenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_archive.Repository);
        A.CallTo(() => _tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
        SetupArchive(WindowedSnapshot(Quarter));
    }

    // ------------------------------------------------------------------ helpers

    private static ArchiveSnapshot WindowedSnapshot(TimeSpan? period)
        => new(TestArchiveRtId, TestCkTypeId, CkArchiveStatus.Activated, "meters",
            [new CkArchiveColumnSpec("Amount.Value", false, false), new CkArchiveColumnSpec("Amount.Unit", false, false)])
        {
            IsTimeRange = true,
            Period = period
        };

    private void SetupArchive(ArchiveSnapshot? snapshot)
        => A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._)).Returns(Task.FromResult(snapshot));

    private static DetectStreamDataGapsNodeConfiguration Config(DateTime from, DateTime to, params OctoObjectId[] series)
        => new()
        {
            ArchiveRtId = TestArchiveRtId,
            TargetPath = "$.gaps",
            From = from,
            To = to,
            RtIds = series.Select(s => s.ToString()).ToList()
        };

    private async Task<(StreamDataGapDetectionResult Result, IPipelineLogger Logger)> RunAsync(
        DetectStreamDataGapsNodeConfiguration config)
    {
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        StreamDataGapDetectionResult? captured = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is StreamDataGapDetectionResult r) captured = r;
            });

        await new DetectStreamDataGapsNode(next, _etlContext, _systemContext).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        return (captured ?? throw new InvalidOperationException("No result written."), logger);
    }

    private async Task<string> RunExpectingErrorAsync(DetectStreamDataGapsNodeConfiguration config)
    {
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() =>
            new DetectStreamDataGapsNode(next, _etlContext, _systemContext).ProcessObjectAsync(dataContext, nodeContext));
        return ex.Message;
    }

    private static void AssertRange(StreamDataGapDetectionRange range, DateTime start, DateTime end, int missing)
    {
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.Equal(missing, range.MissingCount);
    }

    private static bool HasWarning(IPipelineLogger logger, string fragment)
        => Fake.GetCalls(logger).Any(call => call.Arguments.OfType<string>().Any(a => a.Contains(fragment)));

    // ------------------------------------------------------------------ ranges and counts

    [Fact]
    public async Task PartlyFilledDay_WithSeveralHoles_ReportsEveryHoleExactly_AndReadsOnlyThatDay()
    {
        // Day 1: 24 of 96 values present (06:00-12:00 local), day 2 complete.
        _archive.AddSlots(SeriesA, Day1.AddHours(6), Day1.AddHours(12), Quarter);
        _archive.AddSlots(SeriesA, Day1.AddDays(1), Day1.AddDays(2), Quarter);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(2), SeriesA));

        var series = Assert.Single(result.Series);
        Assert.Equal(2, series.Ranges.Count);
        AssertRange(series.Ranges[0], Day1, Day1.AddHours(6), 24);
        AssertRange(series.Ranges[1], Day1.AddHours(12), Day1.AddDays(1), 48);
        Assert.Equal(72, series.MissingCount);
        Assert.Equal(192, result.ExpectedCount);
        Assert.Equal(1, _archive.CountQueries);
        // Only day 1 is read row by row; the complete day 2 is answered by the count.
        Assert.All(_archive.RowQueryRanges, r => Assert.True(r.To <= Day1.AddDays(1)));
        Assert.Equal(24, _archive.RowsReturned);
    }

    [Fact]
    public async Task HolesInsideADay_AreReportedAsSeparateRanges()
    {
        var holes = new[] { Day1.AddHours(3), Day1.AddHours(3).Add(Quarter), Day1.AddHours(17) };
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter, skip: holes);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(1), SeriesA));

        var series = Assert.Single(result.Series);
        Assert.Equal(2, series.Ranges.Count);
        AssertRange(series.Ranges[0], Day1.AddHours(3), Day1.AddHours(3).AddMinutes(30), 2);
        AssertRange(series.Ranges[1], Day1.AddHours(17), Day1.AddHours(17).Add(Quarter), 1);
        Assert.Equal(3, series.MissingCount);
    }

    [Fact]
    public async Task AWholeMissingDay_IsOneRange_WithoutReadingAnyRow()
    {
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter);
        _archive.AddSlots(SeriesA, Day1.AddDays(2), Day1.AddDays(3), Quarter);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(3), SeriesA));

        var series = Assert.Single(result.Series);
        AssertRange(Assert.Single(series.Ranges), Day1.AddDays(1), Day1.AddDays(2), 96);
        Assert.Empty(_archive.RowQueryRanges);
    }

    [Fact]
    public async Task ASeriesWithoutAnyData_IsOneRangeOverTheWholePeriod()
    {
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(2), Quarter);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(2), SeriesA, SeriesB));

        var series = Assert.Single(result.Series);
        Assert.Equal(SeriesB.ToString(), series.RtId);
        AssertRange(Assert.Single(series.Ranges), Day1, Day1.AddDays(2), 192);
        Assert.Equal(2, result.SeriesCount);
        Assert.Equal(1, result.SeriesWithGapsCount);
        Assert.Equal(192, result.MissingCount);
    }

    [Fact]
    public async Task AGapAcrossMidnight_IsMergedIntoOneRange()
    {
        // Missing 23:00 day 1 .. 01:00 day 2 (local): touches two count bins.
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(2), Quarter,
            skip: Enumerable.Range(0, 8).Select(i => Day1.AddHours(23) + Quarter * i).ToArray());

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(2), SeriesA));

        AssertRange(Assert.Single(Assert.Single(result.Series).Ranges), Day1.AddHours(23), Day1.AddHours(25), 8);
    }

    [Fact]
    public async Task ACalendarMonthGivenWithOffsets_HasExactly2976QuarterHourSlots()
    {
        // July 2026 in local time (UTC+2): local midnight to local midnight.
        var from = DateTimeOffset.Parse("2026-07-01T00:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
        var to = DateTimeOffset.Parse("2026-08-01T00:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;

        var (result, _) = await RunAsync(Config(from, to, SeriesA));

        Assert.Equal(new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc), result.From);
        Assert.Equal(new DateTime(2026, 7, 31, 22, 0, 0, DateTimeKind.Utc), result.To);
        Assert.Equal(2976, result.ExpectedCount);
        Assert.Equal(2976, Assert.Single(result.Series).MissingCount);
    }

    [Fact]
    public async Task APeriodAcrossTheDstChange_CountsByInstants()
    {
        // October 2026 in Vienna: 31 days + the extra hour of the autumn change = 2980 slots.
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
        var to = DateTimeOffset.Parse("2026-11-01T00:00:00+01:00", CultureInfo.InvariantCulture).UtcDateTime;
        var hole = DateTimeOffset.Parse("2026-10-25T02:30:00+01:00", CultureInfo.InvariantCulture).UtcDateTime;
        _archive.AddSlots(SeriesA, from, to, Quarter, skip: [hole]);

        var (result, _) = await RunAsync(Config(from, to, SeriesA));

        Assert.Equal(2980, result.ExpectedCount);
        var series = Assert.Single(result.Series);
        AssertRange(Assert.Single(series.Ranges), hole, hole.Add(Quarter), 1);
        // 31 whole 24-hour bins, the extra hour at the end is read row by row.
        Assert.Equal(1, _archive.CountQueries);
        Assert.Contains(_archive.RowQueryRanges, r => r.From == from.AddDays(31) && r.To == to);
    }

    [Fact]
    public async Task HourlySlots_FromAndToAtSixInTheMorningLocal_AreCountedPerHour()
    {
        var from = DateTimeOffset.Parse("2026-07-01T06:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
        var to = DateTimeOffset.Parse("2026-07-03T06:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
        SetupArchive(WindowedSnapshot(Hour));
        _archive.AddSlots(SeriesA, from, to, Hour, skip: [from.AddHours(30), from.AddHours(31)]);

        var (result, _) = await RunAsync(Config(from, to, SeriesA));

        Assert.Equal(48, result.ExpectedCount);
        Assert.Equal("PT1H", result.Interval);
        AssertRange(Assert.Single(Assert.Single(result.Series).Ranges), from.AddHours(30), from.AddHours(32), 2);
        Assert.Equal(TimeSpan.FromHours(4), _archive.LastCountOptions!.BinOrigin);
    }

    [Fact]
    public async Task ExpectedInterval_WinsOverTheArchivePeriod()
    {
        SetupArchive(WindowedSnapshot(Hour));
        _archive.AddSlots(SeriesA, Day1, Day1.AddHours(1), Quarter);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddHours(2), SeriesA) with { ExpectedInterval = Quarter });

        Assert.Equal("PT15M", result.Interval);
        AssertRange(Assert.Single(Assert.Single(result.Series).Ranges), Day1.AddHours(1), Day1.AddHours(2), 4);
    }

    [Fact]
    public async Task AnArchiveWhosePeriodIsNotTheSlot_IsReadRowByRow_SoShortWindowsCannotHideAHole()
    {
        // 94 quarter-hours plus two 5-minute windows: 96 windows in the day, but 20 minutes missing.
        SetupArchive(WindowedSnapshot(Hour));
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter,
            skip: [Day1.AddHours(10), Day1.AddHours(10).Add(Quarter)]);
        _archive.Add(SeriesA, Day1.AddHours(10), Day1.AddHours(10).AddMinutes(5));
        _archive.Add(SeriesA, Day1.AddHours(10).AddMinutes(20), Day1.AddHours(10).AddMinutes(25));

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(1), SeriesA) with { ExpectedInterval = Quarter });

        Assert.Equal(0, _archive.CountQueries);
        var series = Assert.Single(result.Series);
        Assert.Equal(2, series.Ranges.Count);
        AssertRange(series.Ranges[0], Day1.AddHours(10).AddMinutes(5), Day1.AddHours(10).AddMinutes(20), 1);
        AssertRange(series.Ranges[1], Day1.AddHours(10).AddMinutes(25), Day1.AddHours(10).AddMinutes(30), 1);
    }

    // ------------------------------------------------------------------ field filters

    [Fact]
    public async Task FieldFilters_ReachTheCountAndTheRowRead_AndOnlyMatchingRowsCountAsPresent()
    {
        // Every value present, but 06:00-07:00 carries the wrong unit.
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter,
            unit: t => t >= Day1.AddHours(6) && t < Day1.AddHours(7) ? 2 : 1);
        var config = Config(Day1, Day1.AddDays(1), SeriesA) with
        {
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "Amount.Unit",
                    Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = 1
                }
            ]
        };

        var (result, _) = await RunAsync(config);

        AssertRange(Assert.Single(Assert.Single(result.Series).Ranges), Day1.AddHours(6), Day1.AddHours(7), 4);
        Assert.Single(_archive.LastCountOptions!.FieldFilters!);
        Assert.Single(_archive.LastRowOptions!.FieldFilters!);
    }

    // ------------------------------------------------------------------ no row limit, batching

    [Fact]
    public async Task MoreThan200000Rows_AreReadCompletely_ByPagingWithoutAnyWarning()
    {
        // 300 series x 8 days of quarter-hours = 230,400 windows, all read row by row: the count
        // bins are refused for this archive.
        _archive.RefuseCounts = true;
        var series = Enumerable.Range(1, 300).Select(i => new OctoObjectId($"{i:x24}")).ToArray();
        var holeSeries = series[150];
        foreach (var s in series)
        {
            _archive.AddSlots(s, Day1, Day1.AddDays(8), Quarter, skip: s == holeSeries ? [Day1.AddDays(5)] : null);
        }

        var (result, logger) = await RunAsync(Config(Day1, Day1.AddDays(8), series));

        Assert.True(_archive.RowsReturned > 200_000);
        Assert.True(_archive.RowQueryRanges.Count > 1);
        var gappy = Assert.Single(result.Series);
        Assert.Equal(holeSeries.ToString(), gappy.RtId);
        AssertRange(Assert.Single(gappy.Ranges), Day1.AddDays(5), Day1.AddDays(5).Add(Quarter), 1);
        Assert.Equal(300, result.SeriesCount);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ARowReadLargerThanOnePage_IsPagedToTheEnd()
    {
        // Three series but pages of two rows: every slice needs a second page.
        _archive.RefuseCounts = true;
        _archive.AddSlots(SeriesA, Day1, Day1.AddHours(2), Quarter);
        _archive.AddSlots(SeriesB, Day1, Day1.AddHours(2), Quarter, skip: [Day1.AddMinutes(45)]);
        _archive.AddSlots(SeriesC, Day1, Day1.AddHours(2), Quarter);
        var nodeContext = PrepareTest(Config(Day1, Day1.AddHours(2))).NodeContext;
        var detector = new StreamDataGapDetector(_archive.Repository, WindowedSnapshot(Quarter), TestArchiveRtId,
            nodeContext, pageRows: 2);

        var result = await detector.DetectAsync(
            new StreamDataGapDetector.Request(Day1, Day1.AddHours(2), Quarter, [SeriesA, SeriesB, SeriesC], null));

        Assert.Equal(23, _archive.RowsReturned);
        Assert.True(detector.RowQueries > 8);
        var gappy = Assert.Single(result.Series);
        Assert.Equal(SeriesB.ToString(), gappy.RtId);
        AssertRange(Assert.Single(gappy.Ranges), Day1.AddMinutes(45), Day1.AddHours(1), 1);
    }

    [Fact]
    public async Task ManySeries_AreCountedInBatches_ThatStayBelowTheStorageCap()
    {
        var series = Enumerable.Range(1, 25).Select(i => new OctoObjectId($"{i:x24}")).ToList();
        foreach (var s in series.Skip(1))
        {
            _archive.AddSlots(s, Day1, Day1.AddDays(3), Quarter);
        }

        var nodeContext = PrepareTest(Config(Day1, Day1.AddDays(3))).NodeContext;
        var detector = new StreamDataGapDetector(_archive.Repository, WindowedSnapshot(Quarter), TestArchiveRtId,
            nodeContext, maxSeriesPerBatch: 10, pageRows: 100);

        var result = await detector.DetectAsync(
            new StreamDataGapDetector.Request(Day1, Day1.AddDays(3), Quarter, series, null));

        Assert.Equal(3, detector.CountQueries);
        Assert.All(_archive.CountBatchSizes, size => Assert.True(size <= 10));
        var missing = Assert.Single(result.Series);
        Assert.Equal(series[0].ToString(), missing.RtId);
        Assert.Equal(25, result.SeriesCount);
    }

    [Fact]
    public async Task ALongPeriod_ShrinksTheBatch_SoBinsTimesSeriesStayWithinTheStorageCap()
    {
        var series = Enumerable.Range(1, 400).Select(i => new OctoObjectId($"{i:x24}")).ToList();
        var nodeContext = PrepareTest(Config(Day1, Day1.AddDays(400))).NodeContext;
        var detector = new StreamDataGapDetector(_archive.Repository, WindowedSnapshot(Quarter), TestArchiveRtId,
            nodeContext);

        await detector.DetectAsync(new StreamDataGapDetector.Request(Day1, Day1.AddDays(400), Quarter, series, null));

        // 100,000 / 400 days = 250 series per COUNT.
        Assert.Equal([250, 150], _archive.CountBatchSizes);
    }

    // ------------------------------------------------------------------ output

    [Fact]
    public async Task Output_IsSortedBySeriesThenStart_OmitsCompleteSeries_AndIsTheSameOnEveryRun()
    {
        _archive.AddSlots(SeriesC, Day1, Day1.AddDays(1), Quarter, skip: [Day1.AddHours(20), Day1.AddHours(2)]);
        _archive.AddSlots(SeriesB, Day1, Day1.AddDays(1), Quarter);
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter, skip: [Day1.AddHours(9)]);

        var (first, _) = await RunAsync(Config(Day1, Day1.AddDays(1), SeriesC, SeriesB, SeriesA, SeriesC));
        var (second, _) = await RunAsync(Config(Day1, Day1.AddDays(1), SeriesC, SeriesB, SeriesA, SeriesC));

        Assert.Equal([SeriesA.ToString(), SeriesC.ToString()], first.Series.Select(s => s.RtId));
        Assert.Equal(3, first.SeriesCount);
        Assert.True(first.Series[1].Ranges[0].Start < first.Series[1].Ranges[1].Start);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public async Task RtIdsPathResolvingToNothing_WritesAnEmptyResultAndWarns()
    {
        var config = Config(Day1, Day1.AddDays(1)) with { RtIds = null, RtIdsPath = "$.series" };

        var (result, logger) = await RunAsync(config);

        Assert.Empty(result.Series);
        Assert.Equal(0, result.SeriesCount);
        Assert.True(HasWarning(logger, "RtIdsPath"));
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public async Task OverlappingWindows_AreCountedOnce_AndWarnedAboutNamingTheSeries()
    {
        // Day 1 complete plus an hour window over 12:00-13:00 on top of its quarter-hours (97 windows,
        // so the day is read row by row); day 2 misses one value.
        _archive.AddSlots(SeriesA, Day1, Day1.AddDays(1), Quarter);
        _archive.Add(SeriesA, Day1.AddHours(12), Day1.AddHours(13));
        _archive.AddSlots(SeriesA, Day1.AddDays(1), Day1.AddDays(2), Quarter, skip: [Day1.AddDays(1).AddHours(10)]);

        var (result, logger) = await RunAsync(Config(Day1, Day1.AddDays(2), SeriesA));

        var series = Assert.Single(result.Series);
        Assert.True(series.HasOverlaps);
        Assert.Equal(1, series.MissingCount);
        AssertRange(Assert.Single(series.Ranges), Day1.AddDays(1).AddHours(10), Day1.AddDays(1).AddHours(10).Add(Quarter), 1);
        Assert.True(HasWarning(logger, SeriesA.ToString()));
    }

    [Fact]
    public async Task WindowsAcrossABinBoundary_FallBackToTheRowRead_WithTheSameResult()
    {
        // Half-hour phase shift: every window still 15 minutes, but one straddles the bin at 22:00Z.
        _archive.AddSlots(SeriesA, Day1.AddMinutes(-7), Day1.AddDays(2).AddMinutes(-7), Quarter,
            skip: [Day1.AddHours(5).AddMinutes(-7)]);

        var (result, _) = await RunAsync(Config(Day1, Day1.AddDays(2), SeriesA));

        Assert.Equal(1, _archive.RefusedCounts);
        var series = Assert.Single(result.Series);
        // The phase-shifted series leaves the last 7 minutes uncovered plus the skipped window.
        Assert.Equal(2, series.Ranges.Count);
        AssertRange(series.Ranges[0], Day1.AddHours(5).AddMinutes(-7), Day1.AddHours(5).AddMinutes(8), 1);
        AssertRange(series.Ranges[1], Day1.AddDays(2).AddMinutes(-7), Day1.AddDays(2), 1);
    }

    [Fact]
    public async Task AStorageFailureOfTheRowRead_FailsTheNodeWithTheStorageMessage()
    {
        _archive.RowReadFailure = new InvalidOperationException("relation is not accessible");

        var message = await RunExpectingErrorAsync(Config(Day1, Day1.AddHours(6), SeriesA));

        Assert.Contains("relation is not accessible", message);
    }

    [Fact]
    public async Task AStorageFailureOfTheCount_OtherThanABinRefusal_FailsTheNode()
    {
        _archive.CountFailure = new StreamDataException("cluster unavailable");

        var message = await RunExpectingErrorAsync(Config(Day1, Day1.AddDays(1), SeriesA));

        Assert.Contains("cluster unavailable", message);
    }

    // ------------------------------------------------------------------ parity with GetStreamData@1

    [Fact]
    public async Task Parity_ReturnsTheSameRangesAndCountsAsGetStreamDataGapsOnly()
    {
        var from = Day1;
        var to = Day1.AddDays(3).AddHours(5);
        _archive.AddSlots(SeriesA, from, to, Quarter, skip: [Day1.AddHours(1), Day1.AddDays(2).AddHours(23)]);
        _archive.AddSlots(SeriesB, from.AddDays(1), to, Quarter);
        _archive.AddSlots(SeriesC, from, to, Quarter);
        _archive.Add(SeriesC, Day1.AddDays(2).AddHours(3), Day1.AddDays(2).AddHours(3).AddMinutes(10)); // short extra window
        var series = new[] { SeriesA, SeriesB, SeriesC };

        var (detected, _) = await RunAsync(Config(from, to, series));

        var reference = await RunGetStreamDataGapsOnlyAsync(from, to, series);

        var expected = reference.Series
            .Where(s => s.Gaps.Count != 0)
            .OrderBy(s => s.RtId!.ToString(), StringComparer.Ordinal)
            .Select(s => (RtId: s.RtId!.ToString()!, s.MissingIntervals,
                string.Join(";", s.Gaps.Select(g => $"{g.From:O}/{g.To:O}/{g.MissingIntervals}"))))
            .ToList();
        var actual = detected.Series
            .Select(s => (s.RtId, (int?)s.MissingCount,
                string.Join(";", s.Ranges.Select(r => $"{r.Start:O}/{r.End:O}/{r.MissingCount}"))))
            .ToList();
        Assert.Equal(expected, actual);
        Assert.NotEmpty(actual);
    }

    private async Task<StreamDataGapReport> RunGetStreamDataGapsOnlyAsync(DateTime from, DateTime to,
        IEnumerable<OctoObjectId> series)
    {
        var config = new GetStreamDataNodeConfiguration
        {
            ArchiveRtId = TestArchiveRtId,
            TargetPath = "$.data",
            GapsTargetPath = "$.report",
            GapsOnly = true,
            From = from,
            To = to,
            RtIds = series.Select(s => s.ToString()).ToList()
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        StreamDataGapReport? report = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is StreamDataGapReport r) report = r;
            });

        await new GetStreamDataNode(next, _etlContext, _systemContext).ProcessObjectAsync(dataContext, nodeContext);
        return report!;
    }

    // ------------------------------------------------------------------ configuration errors

    [Fact]
    public async Task ARawArchive_FailsNamingArchiveRtId()
    {
        SetupArchive(new ArchiveSnapshot(TestArchiveRtId, TestCkTypeId, CkArchiveStatus.Activated, "raw", []));

        Assert.Contains("ArchiveRtId", await RunExpectingErrorAsync(Config(Day1, Day1.AddDays(1), SeriesA)));
    }

    [Fact]
    public async Task FromNotBeforeTo_FailsNamingFromAndTo()
    {
        var message = await RunExpectingErrorAsync(Config(Day1, Day1, SeriesA));

        Assert.Contains("From", message);
        Assert.Contains("To", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public async Task ANonPositiveExpectedInterval_FailsNamingExpectedInterval(int minutes)
    {
        var config = Config(Day1, Day1.AddDays(1), SeriesA) with { ExpectedInterval = TimeSpan.FromMinutes(minutes) };

        Assert.Contains("ExpectedInterval", await RunExpectingErrorAsync(config));
    }

    [Fact]
    public async Task MoreSlotsThanTheCountsCanHold_FailNamingExpectedInterval()
    {
        // One tick per slot over a day: about 864 billion slots.
        var config = Config(Day1, Day1.AddDays(1), SeriesA) with { ExpectedInterval = TimeSpan.FromTicks(1) };

        Assert.Contains("ExpectedInterval", await RunExpectingErrorAsync(config));
    }

    [Fact]
    public async Task AVeryShortSlot_DoesNotMultiplyTheRowQueries()
    {
        // One-second slots over two days of an empty archive: hour-long slices, not 50-second ones.
        var config = Config(Day1, Day1.AddDays(2), SeriesA) with { ExpectedInterval = TimeSpan.FromSeconds(1) };

        var (result, _) = await RunAsync(config);

        Assert.True(_archive.RowQueryRanges.Count <= 48, $"{_archive.RowQueryRanges.Count} row queries");
        Assert.Equal(172_800, Assert.Single(result.Series).MissingCount);
    }

    [Fact]
    public async Task NoIntervalAtAll_FailsNamingExpectedInterval()
    {
        SetupArchive(WindowedSnapshot(null));

        Assert.Contains("ExpectedInterval", await RunExpectingErrorAsync(Config(Day1, Day1.AddDays(1), SeriesA)));
    }

    [Fact]
    public async Task NoSeriesConfigured_FailsNamingRtIds()
    {
        var config = Config(Day1, Day1.AddDays(1)) with { RtIds = null };

        Assert.Contains("RtIds", await RunExpectingErrorAsync(config));
    }

    [Fact]
    public async Task AMissingFrom_FailsNamingFrom()
    {
        var config = Config(Day1, Day1.AddDays(1), SeriesA) with { From = null };

        Assert.Contains("From", await RunExpectingErrorAsync(config));
    }

    [Fact]
    public async Task AMissingTo_FailsNamingTo()
    {
        var config = Config(Day1, Day1.AddDays(1), SeriesA) with { To = null };

        Assert.Contains("ToPath", await RunExpectingErrorAsync(config));
    }

    [Fact]
    public void TheConfiguration_HasNoTimeZoneDayStartOrPerDayOption()
    {
        var names = typeof(DetectStreamDataGapsNodeConfiguration).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Zone", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("DayStart", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("MaxGapScanRows", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ in-memory archive

    /// <summary>
    /// A windowed archive held in memory, answering through a faked <see cref="IStreamDataRepository" />
    /// with the CrateDB semantics the node relies on.
    /// </summary>
    private sealed class InMemoryWindowedArchive
    {
        private readonly List<(OctoObjectId RtId, DateTime Start, DateTime End, int Unit)> _windows = [];

        public IStreamDataRepository Repository { get; } = A.Fake<IStreamDataRepository>();

        public bool RefuseCounts { get; set; }
        public Exception? RowReadFailure { get; set; }
        public Exception? CountFailure { get; set; }
        public int CountQueries { get; private set; }
        public int RefusedCounts { get; private set; }
        public List<int> CountBatchSizes { get; } = [];
        public List<(DateTime From, DateTime To)> RowQueryRanges { get; } = [];
        public long RowsReturned { get; private set; }
        public StreamDataDownsamplingQueryOptions? LastCountOptions { get; private set; }
        public StreamDataQueryOptions? LastRowOptions { get; private set; }

        public InMemoryWindowedArchive()
        {
            A.CallTo(() => Repository.ExecuteQueryAsync(A<OctoObjectId>._, A<StreamDataQueryOptions>._))
                .ReturnsLazily((OctoObjectId _, StreamDataQueryOptions o) => Task.FromResult(Read(o)));
            A.CallTo(() => Repository.ExecuteDownsamplingQueryAsync(A<OctoObjectId>._,
                    A<StreamDataDownsamplingQueryOptions>._))
                .ReturnsLazily((OctoObjectId _, StreamDataDownsamplingQueryOptions o) => Task.FromResult(Count(o)));
        }

        public void Add(OctoObjectId rtId, DateTime start, DateTime end, int unit = 1)
            => _windows.Add((rtId, start, end, unit));

        public void AddSlots(OctoObjectId rtId, DateTime from, DateTime to, TimeSpan slot,
            DateTime[]? skip = null, Func<DateTime, int>? unit = null)
        {
            for (var t = from; t < to; t += slot)
            {
                if (skip?.Contains(t) != true)
                {
                    Add(rtId, t, t + slot, unit?.Invoke(t) ?? 1);
                }
            }
        }

        private IEnumerable<(OctoObjectId RtId, DateTime Start, DateTime End, int Unit)> Matching(
            StreamDataQueryOptionsBase o)
            => _windows.Where(w => (o.RtIds == null || o.RtIds.Contains(w.RtId))
                                   && (o.FieldFilters == null || o.FieldFilters.All(f =>
                                       Convert.ToInt32(f.ComparisonValue, CultureInfo.InvariantCulture) == w.Unit)));

        private StreamDataQueryResult Read(StreamDataQueryOptions o)
        {
            if (RowReadFailure != null) throw RowReadFailure;
            LastRowOptions = o;
            RowQueryRanges.Add((o.From!.Value, o.To!.Value));

            // CrateDB's windowed time filter: the window overlaps [From, To).
            var rows = Matching(o)
                .Where(w => w.Start < o.To && w.End > o.From)
                .OrderBy(w => w.Start).ThenBy(w => w.RtId.ToString()).ThenBy(w => w.End)
                .Skip(o.Offset ?? 0);
            var take = o.PageSize ?? o.Limit;
            var page = (take.HasValue ? rows.Take(take.Value) : rows)
                .Select(w => new StreamDataRow
                {
                    RtId = w.RtId,
                    Timestamp = w.End,
                    Values = new Dictionary<string, object?> { ["window_start"] = w.Start }
                })
                .ToList();
            RowsReturned += page.Count;
            return new StreamDataQueryResult { Rows = page, TotalCount = page.Count };
        }

        private StreamDataQueryResult Count(StreamDataDownsamplingQueryOptions o)
        {
            CountQueries++;
            LastCountOptions = o;
            CountBatchSizes.Add(o.RtIds?.Count ?? 0);
            if (CountFailure != null) throw CountFailure;

            var day = TimeSpan.FromDays(1);
            var from = o.From!.Value;
            Assert.Equal(from.TimeOfDay, o.BinOrigin);
            Assert.Equal(day, o.BinInterval);
            Assert.Null(o.TimeZone);
            var days = (int)((o.To!.Value - from).Ticks / day.Ticks);
            Assert.Equal(o.To.Value, from + day * days);
            var inRange = Matching(o).Where(w => w.Start < o.To && w.End > from).ToList();

            // The repository refuses windows that straddle a bin boundary instead of undercounting.
            var straddling = inRange.Count(w =>
            {
                var bin = (w.Start - from).Ticks / day.Ticks;
                var binEnd = from + day * (w.Start < from ? bin - 1 : bin) + day;
                return w.Start < from || w.End > binEnd;
            });
            if (RefuseCounts || straddling > 0)
            {
                RefusedCounts++;
                throw new DownsamplingBinOptionsInvalidException($"{straddling} source window(s) straddle a local-day boundary");
            }

            var withData = inRange.Select(w => w.RtId).Distinct().ToList();
            Assert.True((long)days * withData.Count <= 100_000, "storage cap of bins x series exceeded");

            var rows = new List<StreamDataRow>();
            foreach (var rtId in withData)
            {
                for (var d = 0; d < days; d++)
                {
                    var bin = from + day * d;
                    var count = inRange.Count(w => w.RtId == rtId && w.Start >= bin && w.End <= bin + day);
                    rows.Add(new StreamDataRow
                    {
                        RtId = rtId,
                        Timestamp = bin,
                        Values = new Dictionary<string, object?> { ["window_start_count"] = (long)count }
                    });
                }
            }

            return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
        }
    }
}

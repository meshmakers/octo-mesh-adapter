using System.Diagnostics;
using System.Globalization;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Npgsql;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// <c>DetectStreamDataGaps@1</c> against the real CrateDB time-range archive of
/// <see cref="StreamDataFixture" /> (period 15 minutes, one data column Temperature). Each test writes
/// its own series in September 2026, far from the fixture's own data on 2026-06-01, so the other
/// tests of the archive are not affected.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class DetectStreamDataGapsNodeIntegrationTests(StreamDataFixture fixture)
    : IClassFixture<StreamDataFixture>
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    // 2026-09-01T00:00:00+02:00 .. 2026-09-03T00:00:00+02:00: two local days.
    private static readonly DateTime From =
        DateTimeOffset.Parse("2026-09-01T00:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
    private static readonly DateTime To = From.AddDays(2);

    /// <summary>Only values with a positive temperature count as present.</summary>
    private static readonly FieldFilterWithPathDto ValidOnly = new()
    {
        AttributePath = "Temperature",
        Operator = FieldFilterOperatorDto.GreaterThan,
        ComparisonValue = 0
    };

    [Fact]
    public async Task APartlyFilledDay_WithFieldFilters_ReportsTheExactRanges()
    {
        fixture.EnsureInitialized();
        var (partly, empty) = await SeedTwoDaysAsync();

        var result = await ExecuteAsync(NewConfig(partly, empty));

        result.Should().NotBeNull();
        result!.ExpectedCount.Should().Be(192);
        result.Series.Select(s => s.RtId).Should().Equal(new[] { partly, empty }
            .Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal));

        var p = result.Series.Single(s => s.RtId == partly.ToString());
        p.Ranges.Should().HaveCount(2);
        p.Ranges[0].Start.Should().Be(From.AddHours(6));
        p.Ranges[0].End.Should().Be(From.AddHours(7));
        p.Ranges[0].MissingCount.Should().Be(4, "06:00-07:00 carries invalid values the filter drops");
        p.Ranges[1].Start.Should().Be(From.AddHours(12));
        p.Ranges[1].End.Should().Be(From.AddDays(1));
        p.Ranges[1].MissingCount.Should().Be(48);
        p.MissingCount.Should().Be(52);

        var e = result.Series.Single(s => s.RtId == empty.ToString());
        e.Ranges.Should().ContainSingle();
        e.Ranges[0].Start.Should().Be(From);
        e.Ranges[0].End.Should().Be(To);
        e.MissingCount.Should().Be(192);
    }

    [Fact]
    public async Task TheDayCount_AnswersCompleteDays_SoOnlyThePartlyFilledDayIsRead()
    {
        fixture.EnsureInitialized();
        var (partly, empty) = await SeedTwoDaysAsync();
        var (repository, snapshot) = await ResolveArchiveAsync();
        var nodeContext = NodeContextFor(NewConfig(partly, empty));

        var detector = new StreamDataGapDetector(repository, snapshot, fixture.TimeRangeArchiveRtId, nodeContext);
        var filters = new List<FieldFilter> { new("temperature", FieldFilterOperator.GreaterThan, 0) };
        var result = await detector.DetectAsync(
            new StreamDataGapDetector.Request(From, To, Quarter, [partly, empty], filters));

        detector.CountQueries.Should().Be(1);
        detector.CountFallbacks.Should().Be(0, "the storage must accept the 24-hour bins anchored at From");
        // Day 1 holds 48 windows (44 valid); day 2 is complete and never read.
        detector.RowsRead.Should().Be(44);
        result.MissingCount.Should().Be(52 + 192);
    }

    [Fact]
    public async Task Parity_TheSameRangesAndCountsAsGetStreamDataGapsOnly()
    {
        fixture.EnsureInitialized();
        var (partly, empty) = await SeedTwoDaysAsync();

        var detected = await ExecuteAsync(NewConfig(partly, empty));
        var report = await ExecuteGetStreamDataGapsAsync(partly, empty);

        var expected = report!.Series
            .Where(s => s.Gaps.Count != 0)
            .OrderBy(s => s.RtId!.ToString(), StringComparer.Ordinal)
            .Select(s => $"{s.RtId}:{s.MissingIntervals}:" +
                         string.Join(";", s.Gaps.Select(g => $"{g.From:O}/{g.To:O}/{g.MissingIntervals}")));
        var actual = detected!.Series
            .Select(s => $"{s.RtId}:{s.MissingCount}:" +
                         string.Join(";", s.Ranges.Select(r => $"{r.Start:O}/{r.End:O}/{r.MissingCount}")));
        actual.Should().Equal(expected);
    }

    /// <summary>
    /// AC 10 of AB#9588: 1000 series x one calendar month of quarter-hours (2,976,000 windows, one
    /// value in a thousand missing) generated in the local CrateDB, then the node timed. Runs only with
    /// OCTO_RUN_PERFORMANCE_TESTS=1 — the seeding alone takes a while.
    /// </summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Performance_1000SeriesTimesOneMonth()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OCTO_RUN_PERFORMANCE_TESTS") == "1",
            "Set OCTO_RUN_PERFORMANCE_TESTS=1 to run the performance measurement.");
        fixture.EnsureInitialized();

        var from = DateTimeOffset.Parse("2026-10-01T00:00:00+02:00", CultureInfo.InvariantCulture).UtcDateTime;
        var to = DateTimeOffset.Parse("2026-11-01T00:00:00+01:00", CultureInfo.InvariantCulture).UtcDateTime;
        var slots = (int)((to - from).Ticks / Quarter.Ticks);
        const int seriesCount = 1000;
        // rtIds 'ee' + 22 digits: valid object ids, apart from every other test's series.
        var series = Enumerable.Range(0, seriesCount).Select(i => new OctoObjectId($"ee{i:D22}")).ToList();

        // Optional progress trace for long local runs (the test output only appears at the end).
        var traceFile = Environment.GetEnvironmentVariable("OCTO_PERF_TRACE_FILE");
        void Trace(string line)
        {
            if (!string.IsNullOrWhiteSpace(traceFile))
            {
                File.AppendAllText(traceFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
        }

        var seed = Stopwatch.StartNew();
        // In chunks of 10 series: larger statements trip CrateDB's memory breaker in the
        // test container.
        for (long first = 0; first < (long)seriesCount * slots; first += 10L * slots)
        {
            Trace($"seed from g={first}");
            await SqlAsync(
                $"INSERT INTO {Table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"temperature\") " +
                // BIGINT before the multiplication: slot x 900000 ms overflows an INTEGER after slot 2386.
                $"SELECT CAST({Ms(from)} + CAST(g % {slots} AS BIGINT) * 900000 AS TIMESTAMP WITH TIME ZONE), " +
                $"CAST({Ms(from)} + (CAST(g % {slots} AS BIGINT) + 1) * 900000 AS TIMESTAMP WITH TIME ZONE), " +
                $"'ee' || lpad(CAST(g / {slots} AS TEXT), 22, '0'), '{fixture.TestCkTypeId}', 20.0 " +
                $"FROM generate_series({first}, {first + 10L * slots - 1}) AS t(g) WHERE g % 1000 <> 7");
        }

        await fixture.RefreshArchiveAsync(fixture.TimeRangeArchiveRtId);
        seed.Stop();

        var config = new DetectStreamDataGapsNodeConfiguration
        {
            ArchiveRtId = fixture.TimeRangeArchiveRtId,
            TargetPath = "$.gaps",
            From = from,
            To = to,
            RtIds = series.Select(s => s.ToString()).ToList()
        };

        Trace($"seeded in {seed.Elapsed.TotalSeconds:F0} s");
        await ExecuteAsync(config); // warm-up
        Trace("warm-up done");
        var timings = new List<double>();
        StreamDataGapDetectionResult? result = null;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            result = await ExecuteAsync(config);
            timings.Add(watch.Elapsed.TotalSeconds);
            Trace($"run {i}: {watch.Elapsed.TotalSeconds:F2} s");
        }

        timings.Sort();
        var (repository, snapshot) = await ResolveArchiveAsync();
        var detector = new StreamDataGapDetector(repository, snapshot, fixture.TimeRangeArchiveRtId, NodeContextFor(config));
        var direct = await detector.DetectAsync(new StreamDataGapDetector.Request(from, to, Quarter, series, null));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"AB#9588 perf reads: {detector.CountQueries} count queries ({detector.CountFallbacks} refused), " +
            $"{detector.RowQueries} row queries, {detector.RowsRead} rows read; missing {direct.MissingCount}");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"AB#9588 perf: {seriesCount} series x {slots} slots ({(long)seriesCount * slots} windows, seeded in " +
            $"{seed.Elapsed.TotalSeconds:F0} s): median {timings[1]:F2} s, min {timings[0]:F2} s, max {timings[2]:F2} s; " +
            $"{result!.SeriesWithGapsCount} series with gaps, {result.MissingCount} missing slots");

        result!.SeriesCount.Should().Be(seriesCount);
        result.MissingCount.Should().Be((long)seriesCount * slots / 1000);
    }

    // ------------------------------------------------------------------ seeding

    private string Table => $"\"{fixture.StreamDataTenantId}\".\"archive_{fixture.TimeRangeArchiveRtId}\"";

    private static long Ms(DateTime value) => new DateTimeOffset(value).ToUnixTimeMilliseconds();

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A fresh pair of series over the two days: <c>partly</c> has day 1 00:00-12:00 local (06:00-07:00
    /// with an invalid temperature) and day 2 complete; <c>empty</c> has nothing.
    /// </summary>
    private async Task<(OctoObjectId Partly, OctoObjectId Empty)> SeedTwoDaysAsync()
    {
        var partly = OctoObjectId.GenerateNewId();
        var empty = OctoObjectId.GenerateNewId();
        var (repository, _) = await ResolveArchiveAsync();
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);

        var points = new List<TimeRangeStreamDataPoint>();
        for (var start = From; start < To; start += Quarter)
        {
            if (start >= From.AddHours(12) && start < From.AddDays(1))
            {
                continue;
            }

            var invalid = start >= From.AddHours(6) && start < From.AddHours(7);
            points.Add(new TimeRangeStreamDataPoint
            {
                From = start,
                To = start + Quarter,
                RtId = partly,
                CkTypeId = ckTypeId,
                RtWellKnownName = "GAPS-PARTLY",
                Attributes = new Dictionary<string, object?> { ["Temperature"] = invalid ? -1.0 : 20.0 }
            });
        }

        await repository.InsertTimeRangeAsync(fixture.TimeRangeArchiveRtId, points);
        await fixture.RefreshArchiveAsync(fixture.TimeRangeArchiveRtId);
        return (partly, empty);
    }

    // ------------------------------------------------------------------ execution

    private DetectStreamDataGapsNodeConfiguration NewConfig(params OctoObjectId[] series) => new()
    {
        ArchiveRtId = fixture.TimeRangeArchiveRtId,
        TargetPath = "$.gaps",
        From = From,
        To = To,
        RtIds = series.Select(s => s.ToString()).ToList(),
        FieldFilters = [ValidOnly]
    };

    private async Task<(IStreamDataRepository Repository, ArchiveSnapshot Snapshot)> ResolveArchiveAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw new InvalidOperationException("StreamDataRepository not available.");
        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(fixture.TimeRangeArchiveRtId)
                       ?? throw new InvalidOperationException("Archive snapshot not available.");
        return (repository, snapshot);
    }

    private INodeContext NodeContextFor(DetectStreamDataGapsNodeConfiguration config)
    {
        var dataContext = A.Fake<IDataContext>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        return rootContext.RegisterChildNode("DetectStreamDataGaps", 0, config, dataContext);
    }

    private async Task<StreamDataGapDetectionResult?> ExecuteAsync(DetectStreamDataGapsNodeConfiguration config)
    {
        var systemContext = fixture.GetSystemContext();
        var meshEtlContext = CreateMeshEtlContext(systemContext.GetSystemTenantRepository());

        StreamDataGapDetectionResult? captured = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is StreamDataGapDetectionResult r) captured = r;
            });

        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("DetectStreamDataGaps", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        await new DetectStreamDataGapsNode(Next, meshEtlContext, systemContext).ProcessObjectAsync(dataContext, nodeContext);
        return captured;
    }

    private async Task<StreamDataGapReport?> ExecuteGetStreamDataGapsAsync(params OctoObjectId[] series)
    {
        var config = new GetStreamDataNodeConfiguration
        {
            ArchiveRtId = fixture.TimeRangeArchiveRtId,
            TargetPath = "$.data",
            GapsTargetPath = "$.gaps",
            GapsOnly = true,
            From = From,
            To = To,
            RtIds = series.Select(s => s.ToString()).ToList(),
            FieldFilters = [ValidOnly]
        };
        var systemContext = fixture.GetSystemContext();
        var meshEtlContext = CreateMeshEtlContext(systemContext.GetSystemTenantRepository());

        StreamDataGapReport? captured = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is StreamDataGapReport r) captured = r;
            });

        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetStreamData", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        await new GetStreamDataNode(Next, meshEtlContext, systemContext).ProcessObjectAsync(dataContext, nodeContext);
        return captured;
    }

    private static MeshEtlContext CreateMeshEtlContext(ITenantRepository tenantRepository)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>());
    }
}

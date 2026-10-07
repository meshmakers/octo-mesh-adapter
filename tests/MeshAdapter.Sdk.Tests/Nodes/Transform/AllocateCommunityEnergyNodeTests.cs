using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Drives AllocateCommunityEnergy@1 (AB#5634) end to end over faked repositories: loading,
/// participation (ALC-05), the per-day abort (ALC-08), missing values (ALC-04), the record shape that
/// SaveTimeRangeSeriesInArchive@1 consumes unchanged, determinism and the window guards.
/// </summary>
public class AllocateCommunityEnergyNodeTests : SessionNodeTestBase
{
    private const string TenantId = "test-tenant";
    private const string TargetPath = "$.allocation.records";
    private const string SummaryPath = "$.allocation.summary";
    private const string ConsumerType = "EnergyCommunity/Consumer";
    private const string ProducerType = "EnergyCommunity/Producer";
    private const string PeriodType = "EnergyCommunity/ParticipationPeriod";
    private const string EmType = "Basic.Energy/EnergyMeasurement";
    private const string ConsumerIn = "1-1:1.9.0 G.01";
    private const string ProducerIn = "1-1:2.9.0 G.01";
    private const string ShareObis = "1-1:2.9.0 G.03";
    private const string OfferedObis = "1-1:2.9.0 G.01T";
    private const string SurplusObis = "1-1:2.9.0 P.01T";

    private static readonly OctoObjectId ArchiveRtId = new("ec0000000000000000000a01");
    private const string C1 = "0000000000000000000000c1";
    private const string C2 = "0000000000000000000000c2";
    private const string P1 = "0000000000000000000000a1";

    /// <summary>Day under test: 2026-11-15 in Vienna = [2026-11-14T23:00Z, 2026-11-15T23:00Z).</summary>
    private static readonly DateOnly Day = new(2026, 11, 15);
    private static readonly DateTime DayStartUtc = new(2026, 11, 14, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CutOffUtc = new(2026, 11, 16, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Open = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IStreamDataRepository _streamData = A.Fake<IStreamDataRepository>();
    private readonly ArchiveSnapshot _snapshot;

    // ---- the faked world
    private readonly List<RtEntity> _consumers = [];
    private readonly List<RtEntity> _producers = [];
    private readonly List<(RtEntity Period, string MeteringPoint)> _periods = [];
    private readonly List<(RtEntity Anchor, string MeteringPoint)> _anchors = [];
    private readonly List<(string Anchor, DateTime From, DateTime To, object? Value, object? Quality)> _rows = [];
    private readonly List<StreamDataQueryOptions> _queries = [];

    public AllocateCommunityEnergyNodeTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(TenantId);
        var tenantContext = A.Fake<ITenantContext>();
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_streamData);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);

        _snapshot = new ArchiveSnapshot(ArchiveRtId, new RtCkId<CkTypeId>(EmType), CkArchiveStatus.Activated,
            "energy", [new CkArchiveColumnSpec("Amount.Value", false, false), new CkArchiveColumnSpec("DataQuality", false, false)])
        {
            IsTimeRange = true
        };
        A.CallTo(() => archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(_snapshot));

        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
                Task.FromResult(ResultSet(type.ToString() switch
                {
                    ConsumerType => _consumers,
                    ProducerType => _producers,
                    PeriodType => _periods.Select(p => p.Period).ToList(),
                    EmType => _anchors.Select(a => a.Anchor).ToList(),
                    _ => []
                })));

        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(A<IOctoSession>._,
                A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._, A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._, A<GraphDirections>._, A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily(call =>
            {
                var originType = call.GetArgument<RtCkId<CkTypeId>>(2)!;
                var links = originType.ToString() == PeriodType
                    ? _periods.Select(p => (p.Period, p.MeteringPoint))
                    : _anchors.Select(a => (a.Anchor, a.MeteringPoint));
                return Task.FromResult(MultiResult(originType, links));
            });

        A.CallTo(() => _streamData.ExecuteQueryAsync(A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .ReturnsLazily((OctoObjectId _, StreamDataQueryOptions options) => Task.FromResult(Query(options)));

        // Default world: two consumers, one producer, everything Simulated and participating since 2026.
        AddConsumer(C1, "AT0000000000000000000000000000C01");
        AddConsumer(C2, "AT0000000000000000000000000000C02");
        AddProducer(P1, "AT0000000000000000000000000000P01");
    }

    // ==================================================================== world builders

    private static IResultSet<RtEntity> ResultSet(List<RtEntity> entities)
    {
        var rs = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => rs.Items).Returns(entities.ToList());
        A.CallTo(() => rs.TotalCount).Returns(entities.Count);
        return rs;
    }

    private static IMultipleOriginResultSet<RtEntity> MultiResult(RtCkId<CkTypeId> originType,
        IEnumerable<(RtEntity Origin, string Target)> links)
    {
        var pairs = links
            .GroupBy(l => l.Origin.RtId)
            .Select(g => new KeyValuePair<RtEntityId, IResultSet<RtEntity>>(
                new RtEntityId(originType, g.Key),
                ResultSet(g.Select(l => new RtEntity(new RtCkId<CkTypeId>("Basic.Energy/MeteringPoint"),
                    new OctoObjectId(l.Target))).ToList())))
            .ToList();
        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() => pairs.GetEnumerator());
        return multi;
    }

    private static RtEntity Point(string type, string rtId, string number, int? pf, int? dataSource)
    {
        var e = new RtEntity(new RtCkId<CkTypeId>(type), new OctoObjectId(rtId));
        e.SetAttributeValue("MeteringPointNumber", AttributeValueTypesDto.String, number);
        if (pf is not null) e.SetAttributeValue("PartitionFactor", AttributeValueTypesDto.Int, pf.Value);
        if (dataSource is not null) e.SetAttributeValue("MeteringDataSource", AttributeValueTypesDto.Enum, dataSource.Value);
        return e;
    }

    private int _sequence;

    private string NextId(string prefix) => $"{prefix}{++_sequence:D20}";

    private void AddConsumer(string rtId, string number, int? pf = 100, int? dataSource = 1, DateTime? from = null,
        DateTime? to = null, bool participates = true)
    {
        _consumers.Add(Point(ConsumerType, rtId, number, pf, dataSource));
        AddAnchor(rtId, ConsumerIn);
        if (participates) AddPeriod(rtId, from ?? Open, to);
    }

    private void AddProducer(string rtId, string number, int? pf = 100, int? dataSource = 1, DateTime? from = null,
        DateTime? to = null)
    {
        _producers.Add(Point(ProducerType, rtId, number, pf, dataSource));
        AddAnchor(rtId, ProducerIn);
        AddPeriod(rtId, from ?? Open, to);
    }

    private void AddPeriod(string meteringPoint, DateTime from, DateTime? to)
    {
        var period = new RtEntity(new RtCkId<CkTypeId>(PeriodType), new OctoObjectId(NextId("eeee")));
        var range = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/TimeRange") };
        range.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
        if (to is not null) range.SetAttributeValue("To", AttributeValueTypesDto.DateTime, to.Value);
        period.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, range);
        _periods.Add((period, meteringPoint));
    }

    private string AddAnchor(string meteringPoint, string obis)
    {
        var anchor = new RtEntity(new RtCkId<CkTypeId>(EmType), new OctoObjectId(NextId("dddd")));
        anchor.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, obis);
        _anchors.Add((anchor, meteringPoint));
        return anchor.RtId.ToString();
    }

    private string AnchorOf(string meteringPoint, string obis)
        => _anchors.Single(a => a.MeteringPoint == meteringPoint
                                && a.Anchor.GetAttributeStringValueOrDefault("ObisCode") == obis).Anchor.RtId.ToString();

    /// <summary>One row per slot of <paramref name="days"/> days from <see cref="DayStartUtc"/>.</summary>
    private void Fill(string meteringPoint, string obis, Func<int, object?> value, int days = 1, int quality = 1)
    {
        var anchor = AnchorOf(meteringPoint, obis);
        var start = DayStartUtc;
        var end = DayStartUtc.AddDays(days);
        var i = 0;
        for (var t = start; t < end; t = t.AddMinutes(15), i++)
        {
            _rows.Add((anchor, t, t.AddMinutes(15), value(i), quality));
        }
    }

    private void FillDefault(int days = 1)
    {
        Fill(C1, ConsumerIn, _ => 0.1);
        Fill(C2, ConsumerIn, _ => 0.1);
        Fill(P1, ProducerIn, _ => 0.15);
        if (days > 1)
        {
            _rows.Clear();
            Fill(C1, ConsumerIn, _ => 0.1, days);
            Fill(C2, ConsumerIn, _ => 0.1, days);
            Fill(P1, ProducerIn, _ => 0.15, days);
        }
    }

    /// <summary>The faked archive: honours rtIds, time window overlap, sort (fixed) and paging.</summary>
    private StreamDataQueryResult Query(StreamDataQueryOptions options)
    {
        _queries.Add(options);
        var resolver = StreamDataFieldResolver.CreateForArchive(_snapshot);
        string Key(string name) => resolver.Resolve(name)!.CrateDbName;

        var ids = options.RtIds?.Select(x => x.ToString()).ToHashSet() ?? [];
        var matching = _rows
            .Where(r => ids.Contains(r.Anchor))
            .Where(r => (options.To is null || r.From < options.To) && (options.From is null || r.To > options.From))
            .OrderBy(r => r.From).ThenBy(r => r.Anchor, StringComparer.Ordinal)
            .ToList();

        var page = matching.Skip(options.Offset ?? 0).Take(options.PageSize ?? int.MaxValue)
            .Select(r => new StreamDataRow
            {
                RtId = new OctoObjectId(r.Anchor),
                Timestamp = r.To,
                Values = new Dictionary<string, object?>
                {
                    [Key("window_start")] = r.From,
                    [Key("window_end")] = r.To,
                    [Key("Amount.Value")] = r.Value,
                    [Key("DataQuality")] = r.Quality
                }
            })
            .ToList();

        return new StreamDataQueryResult { Rows = page, TotalCount = matching.Count };
    }

    // ==================================================================== running

    private static AllocateCommunityEnergyNodeConfiguration Config(int numDays = 1) => new()
    {
        ArchiveRtId = ArchiveRtId.ToString(),
        StartDay = Day.ToDateTime(TimeOnly.MinValue),
        NumDays = numDays,
        TargetPath = TargetPath,
        SummaryTargetPath = SummaryPath
    };

    private sealed class Output
    {
        public List<AllocationEnergyData>? Records { get; set; }
        public List<AllocationDaySummary>? Summary { get; set; }
        public IPipelineLogger Logger { get; init; } = null!;
    }

    private async Task<Output> RunAsync(AllocateCommunityEnergyNodeConfiguration config,
        Action<IDataContext>? setupData = null, TimeProvider? clock = null, int? pageSize = null)
    {
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        setupData?.Invoke(dataContext);

        var output = new Output { Logger = logger };
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                switch (call.Arguments[1])
                {
                    case List<AllocationEnergyData> records when (string)call.Arguments[0]! == config.TargetPath:
                        output.Records = records;
                        break;
                    case List<AllocationDaySummary> summary when (string)call.Arguments[0]! == config.SummaryTargetPath:
                        output.Summary = summary;
                        break;
                }
            });

        var node = new AllocateCommunityEnergyNode(next, EtlContext, _systemContext)
        {
            Clock = clock ?? TimeProvider.System,
            PageSize = pageSize ?? AllocateCommunityEnergyNode.ArchivePageSize
        };
        await node.ProcessObjectAsync(dataContext, nodeContext);
        VerifyNextCalled(next, dataContext, nodeContext);
        return output;
    }

    private static JsonArray Serialize<T>(T value)
        => (JsonArray)JsonSerializer.SerializeToNode(value, SystemTextJsonOptions.NodeNavigation)!;

    // ==================================================================== tests

    [Fact]
    public async Task RecordShapeAndOrdering_OneRecordPerPointRegisterAndDay()
    {
        FillDefault();
        var output = await RunAsync(Config());

        var records = output.Records!;
        // Order: rtId ordinal (a1 < c1 < c2), producer offered before surplus.
        Assert.Equal(
            [(P1, OfferedObis), (P1, SurplusObis), (C1, ShareObis), (C2, ShareObis)],
            records.Select(r => (r.MeteringPointRtId, r.MeterCode)));

        foreach (var r in records)
        {
            Assert.Equal("kWh", r.QuantityUnit);
            Assert.Equal(CutOffUtc, r.CreationTime);
            Assert.Equal(DayStartUtc, r.PeriodStart);
            Assert.Equal(DayStartUtc.AddDays(1), r.PeriodEnd);
            Assert.Equal(96, r.EnergyQuantities.Count);
            Assert.Equal(DayStartUtc, r.EnergyQuantities[0].From);
            Assert.True(r.EnergyQuantities.Zip(r.EnergyQuantities.Skip(1)).All(p => p.First.To == p.Second.From));
            Assert.All(r.EnergyQuantities, q =>
            {
                Assert.Equal(r.MeteringPointRtId, q.MeteringPointRtId);
                Assert.Equal(r.MeteringPointNumber, q.MeteringPointNumber);
                Assert.Equal("L1", q.Quality);
            });
        }

        // E = 0.15, W = 0.2 -> 0.075 each, no surplus.
        Assert.All(records[2].EnergyQuantities, q => Assert.Equal(0.075m, q.Quantity));
        Assert.All(records[0].EnergyQuantities, q => Assert.Equal(0.150m, q.Quantity));
        Assert.All(records[1].EnergyQuantities, q => Assert.Equal(0m, q.Quantity));
        Assert.Equal("AT0000000000000000000000000000C01", records[2].MeteringPointNumber);

        var summary = Assert.Single(output.Summary!);
        Assert.Equal("2026-11-15", summary.Day);
        Assert.Equal(AllocationDaySummary.StatusAllocated, summary.Status);
        Assert.Equal(96, summary.Slots);
        Assert.Equal(2, summary.Consumers);
        Assert.Equal(1, summary.Producers);
        Assert.Equal(19.2m, summary.ConsumptionKWh);
        Assert.Equal(14.4m, summary.OfferedKWh);
        Assert.Equal(14.4m, summary.AllocatedKWh);
        Assert.Equal(0m, summary.SurplusKWh);
        Assert.Equal(0.75m, summary.Coverage);
        Assert.Equal(0m, summary.SurplusRatio);
        Assert.Equal(CutOffUtc, summary.CutOff);
        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task Records_AreConsumedBySaveTimeRangeSeriesInArchiveWithTheEdaConfiguration()
    {
        FillDefault();
        var output = await RunAsync(Config());

        // Exactly the configuration of handle-daten-crmsg.yaml:230-257.
        var save = new SaveTimeRangeSeriesInArchiveNodeConfiguration
        {
            Path = TargetPath,
            ArchiveRtId = ArchiveRtId.ToString(),
            CkTypeId = EmType,
            ValuesProperty = "EnergyQuantities",
            WellKnownNameFormat = "{MeteringPointRtId}_{MeterCode}",
            FromProperty = "From",
            ToProperty = "To",
            AnchorWindowFromAttribute = "TimeRange.From",
            AnchorWindowToAttribute = "TimeRange.To",
            ParentRtIdProperty = "MeteringPointRtId",
            ParentCkTypeId = "Basic.Energy/MeteringPoint",
            ParentAssociationRoleId = "System/ParentChild",
            Columns =
            [
                new TimeRangeSeriesColumn { Name = "Amount.Value", ValueProperty = "Quantity" },
                new TimeRangeSeriesColumn { Name = "Amount.Unit", ValueProperty = "QuantityUnit", Scope = TimeRangeSeriesColumnScope.Series },
                new TimeRangeSeriesColumn { Name = "DataQuality", ValueProperty = "Quality" },
                new TimeRangeSeriesColumn { Name = "ObisCode", ValueProperty = "MeterCode", Scope = TimeRangeSeriesColumnScope.Series },
                new TimeRangeSeriesColumn { Name = "SourceDocumentDate", ValueProperty = "CreationTime", Scope = TimeRangeSeriesColumnScope.Series }
            ]
        };

        var json = Serialize(output.Records);
        var series = TimeRangeSeriesShaper.Shape(json, save, out var unresolved);

        Assert.Equal(0, unresolved);
        Assert.Equal(
            [$"{P1}_{OfferedObis}", $"{P1}_{SurplusObis}", $"{C1}_{ShareObis}", $"{C2}_{ShareObis}"],
            series.Select(s => s.WellKnownName));
        Assert.All(series, s => Assert.Equal(s.Series["MeteringPointRtId"]!.GetValue<string>(),
            s.WellKnownName[..24]));

        foreach (var s in series)
        {
            s.RtId = OctoObjectId.GenerateNewId();
        }

        var rows = TimeRangeSeriesShaper.BuildRows(series, new RtCkId<CkTypeId>(EmType), save,
            (_, raw) => raw, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(4 * 96, rows.Count);
        var first = rows.First(r => r.RtWellKnownName == $"{C1}_{ShareObis}");
        Assert.Equal(DayStartUtc, first.From);
        Assert.Equal(DayStartUtc.AddMinutes(15), first.To);
        Assert.Equal(0.075m, Convert.ToDecimal(first.Attributes["Amount.Value"]));
        Assert.Equal("kWh", first.Attributes["Amount.Unit"]);
        Assert.Equal("L1", first.Attributes["DataQuality"]);
        Assert.Equal(ShareObis, first.Attributes["ObisCode"]);
        Assert.Equal(CutOffUtc, first.Attributes["SourceDocumentDate"]);
    }

    [Fact]
    public async Task PointJoiningMidDay_HasNoQuantitiesBeforeItsStart()
    {
        _consumers.Clear();
        _periods.RemoveAll(p => p.MeteringPoint is C1 or C2);
        _anchors.RemoveAll(a => a.MeteringPoint is C1 or C2);
        AddConsumer(C1, "AT-C1");
        var joins = DayStartUtc.AddHours(12); // 2026-11-15T11:00Z
        AddConsumer(C2, "AT-C2", from: joins);
        FillDefault();

        var output = await RunAsync(Config());

        var c2 = output.Records!.Single(r => r.MeteringPointRtId == C2);
        Assert.Equal(48, c2.EnergyQuantities.Count);
        Assert.Equal(joins, c2.EnergyQuantities[0].From);

        // Before C2 joins C1 is alone: W = 0.1 <= E = 0.15 -> 0.1; afterwards 0.075.
        var c1 = output.Records!.Single(r => r.MeteringPointRtId == C1);
        Assert.Equal(0.1m, c1.EnergyQuantities[0].Quantity);
        Assert.Equal(0.075m, c1.EnergyQuantities[^1].Quantity);
        var surplus = output.Records!.Single(r => r.MeterCode == SurplusObis);
        Assert.Equal(0.05m, surplus.EnergyQuantities[0].Quantity);
        Assert.Equal(0m, surplus.EnergyQuantities[^1].Quantity);
    }

    [Fact]
    public async Task PointJoiningOnALaterDay_HasNoRecordBefore()
    {
        _consumers.RemoveAll(c => c.RtId.ToString() == C2);
        _periods.RemoveAll(p => p.MeteringPoint == C2);
        _anchors.RemoveAll(a => a.MeteringPoint == C2);
        AddConsumer(C2, "AT-C2", from: DayStartUtc.AddDays(1)); // local 2026-11-16 00:00
        FillDefault(days: 2);

        var output = await RunAsync(Config(numDays: 2));

        var c2 = output.Records!.Where(r => r.MeteringPointRtId == C2).ToList();
        var single = Assert.Single(c2);
        Assert.Equal(DayStartUtc.AddDays(1), single.PeriodStart);
        Assert.Equal(96, single.EnergyQuantities.Count);
        Assert.Equal([1, 2], output.Summary!.Select(s => s.Consumers));
    }

    [Fact]
    public async Task PeriodEndingMidDay_StopsAtItsEnd()
    {
        _periods.RemoveAll(p => p.MeteringPoint == C2);
        AddPeriod(C2, Open, DayStartUtc.AddHours(6));
        FillDefault();

        var output = await RunAsync(Config());

        var c2 = output.Records!.Single(r => r.MeteringPointRtId == C2);
        Assert.Equal(24, c2.EnergyQuantities.Count);
        Assert.Equal(DayStartUtc.AddHours(6), c2.EnergyQuantities[^1].To);
    }

    [Fact]
    public async Task EdaPointParticipatingOnADay_AbortsOnlyThatDay()
    {
        AddConsumer("0000000000000000000000c3", "AT-EDA-C3", dataSource: 0, from: DayStartUtc.AddDays(1));
        FillDefault(days: 2);

        var output = await RunAsync(Config(numDays: 2));

        Assert.Equal(["allocated", "aborted"], output.Summary!.Select(s => s.Status));
        Assert.Contains("AT-EDA-C3", output.Summary![1].Reason);
        Assert.All(output.Records!, r => Assert.Equal(DayStartUtc, r.PeriodStart));
        Assert.Equal(4, output.Records!.Count);
        A.CallTo(() => output.Logger.Warning(A<string>._, A<string>._,
            A<string>.That.Contains("AT-EDA-C3"), A<object[]>._)).MustHaveHappened();
    }

    [Fact]
    public async Task MissingDataSourceAttribute_IsTreatedAsEda_AndAborts()
    {
        AddConsumer("0000000000000000000000c3", "AT-UNKNOWN", dataSource: null);
        FillDefault();

        var output = await RunAsync(Config());

        Assert.Empty(output.Records!);
        var summary = Assert.Single(output.Summary!);
        Assert.Equal(AllocationDaySummary.StatusAborted, summary.Status);
        Assert.Contains("AT-UNKNOWN", summary.Reason);
        // An aborted day does not even read the archive.
        Assert.Empty(_queries);
    }

    [Fact]
    public async Task NonParticipatingEdaPoint_DoesNotAbort()
    {
        AddConsumer("0000000000000000000000c3", "AT-EDA", dataSource: 0, participates: false);
        FillDefault();

        var output = await RunAsync(Config());

        Assert.Equal(AllocationDaySummary.StatusAllocated, Assert.Single(output.Summary!).Status);
        Assert.DoesNotContain(output.Records!, r => r.MeteringPointRtId == "0000000000000000000000c3");
    }

    [Fact]
    public async Task NobodyParticipating_DayIsEmpty()
    {
        _periods.Clear();
        FillDefault();

        var output = await RunAsync(Config());

        Assert.Empty(output.Records!);
        Assert.Equal(AllocationDaySummary.StatusEmpty, Assert.Single(output.Summary!).Status);
    }

    [Fact]
    public async Task ProducerWithoutData_GetsBothRegistersOnEverySlot_WithMissingQuality()
    {
        Fill(C1, ConsumerIn, _ => 0.1);
        // C2 and P1 deliver nothing at all.

        var output = await RunAsync(Config());

        var offered = output.Records!.Single(r => r.MeterCode == OfferedObis);
        var surplus = output.Records!.Single(r => r.MeterCode == SurplusObis);
        Assert.Equal(96, offered.EnergyQuantities.Count);
        Assert.Equal(96, surplus.EnergyQuantities.Count);
        Assert.All(offered.EnergyQuantities.Concat(surplus.EnergyQuantities), q =>
        {
            Assert.Equal(0m, q.Quantity);
            Assert.Equal("L3", q.Quality);
        });

        var c2 = output.Records!.Single(r => r.MeteringPointRtId == C2);
        Assert.All(c2.EnergyQuantities, q => Assert.Equal("L3", q.Quality));
        // V-2: C1 delivered L1, but the slot has missing inputs, so its share is L3 as well.
        var c1 = output.Records!.Single(r => r.MeteringPointRtId == C1);
        Assert.All(c1.EnergyQuantities, q => Assert.Equal("L3", q.Quality));
        Assert.Equal(96, output.Summary![0].MissingProducerValues);
        Assert.Equal(96, output.Summary![0].MissingConsumerValues);
        Assert.Null(output.Summary![0].SurplusRatio);
    }

    [Fact]
    public async Task ProducerWithZeroGeneration_StillWritesBothRegisters()
    {
        Fill(C1, ConsumerIn, _ => 0.1);
        Fill(C2, ConsumerIn, _ => 0.1);
        Fill(P1, ProducerIn, i => i < 48 ? 0.0 : 0.3, quality: 2);

        var output = await RunAsync(Config());

        var offered = output.Records!.Single(r => r.MeterCode == OfferedObis);
        var surplus = output.Records!.Single(r => r.MeterCode == SurplusObis);
        Assert.Equal(96, offered.EnergyQuantities.Count);
        Assert.Equal(96, surplus.EnergyQuantities.Count);
        Assert.Equal(0m, offered.EnergyQuantities[0].Quantity);
        Assert.Equal("L2", offered.EnergyQuantities[0].Quality);
        // V-2: the L1 consumers take the worst input quality of the slot (the L2 producer).
        Assert.All(output.Records!.Where(r => r.MeteringPointRtId == C1 || r.MeteringPointRtId == C2)
            .SelectMany(r => r.EnergyQuantities), q => Assert.Equal("L2", q.Quality));
        Assert.Equal(0.3m, offered.EnergyQuantities[^1].Quantity);
        Assert.Equal(0.1m, surplus.EnergyQuantities[^1].Quantity);
    }

    [Fact]
    public async Task ManualInput_IsReadAsManual_AndMarksTheWholeSlot()
    {
        // Basic.Energy DataQuality key 4 = Manual (e.g. /manualMeteringEntry). It must not pass as L1:
        // the slot carries the worst input quality (V-2) and Manual ranks below L2.
        Fill(C1, ConsumerIn, _ => 0.1);
        Fill(C2, ConsumerIn, _ => 0.1, quality: 2);
        Fill(P1, ProducerIn, _ => 0.3, quality: 4);

        var output = await RunAsync(Config());

        Assert.All(output.Records!.SelectMany(r => r.EnergyQuantities), q => Assert.Equal("Manual", q.Quality));
    }

    [Fact]
    public async Task PartitionFactorFromTheEntity_IsApplied()
    {
        _producers.Clear();
        _producers.Add(Point(ProducerType, P1, "AT-P1", pf: 50, dataSource: 2));
        FillDefault();

        var output = await RunAsync(Config());

        // offered = 0.5 * 0.15 = 0.075, W = 0.2 -> 0.0375 each -> floor 0.037, surplus 0.001.
        Assert.Equal(0.075m, output.Records!.Single(r => r.MeterCode == OfferedObis).EnergyQuantities[0].Quantity);
        Assert.Equal(0.037m, output.Records!.Single(r => r.MeteringPointRtId == C1).EnergyQuantities[0].Quantity);
        Assert.Equal(0.001m, output.Records!.Single(r => r.MeterCode == SurplusObis).EnergyQuantities[0].Quantity);
    }

    [Fact]
    public async Task RowsThatAreNotASlotOfTheDay_AreIgnored()
    {
        FillDefault();
        _rows.RemoveAll(r => r.Anchor == AnchorOf(C1, ConsumerIn) && r.From == DayStartUtc);
        _rows.Add((AnchorOf(C1, ConsumerIn), DayStartUtc, DayStartUtc.AddMinutes(30), 5.0, 1));

        var output = await RunAsync(Config());

        var c1 = output.Records!.Single(r => r.MeteringPointRtId == C1);
        Assert.Equal(0m, c1.EnergyQuantities[0].Quantity);
        Assert.Equal("L3", c1.EnergyQuantities[0].Quality);
    }

    [Fact]
    public async Task ArchiveRead_IsPaged_AndLosesNoRows()
    {
        FillDefault(days: 2);

        var paged = await RunAsync(Config(numDays: 2), pageSize: 50);
        var pagedJson = Serialize(paged.Records).ToJsonString();
        var pagedQueries = _queries.Count;
        _queries.Clear();
        var whole = await RunAsync(Config(numDays: 2));

        // 2 days x 96 slots x 3 anchors = 576 rows -> 12 pages of 50.
        Assert.Equal(12, pagedQueries);
        Assert.Single(_queries);
        Assert.Equal(Serialize(whole.Records).ToJsonString(), pagedJson);
        Assert.All(paged.Records!.SelectMany(r => r.EnergyQuantities), q => Assert.Equal("L1", q.Quality));
    }

    [Fact]
    public async Task ArchiveQuery_IsScopedToInputAnchorsAndTheWindow()
    {
        AddAnchor(C1, ShareObis); // an output anchor must never be read as input
        FillDefault();

        await RunAsync(Config());

        var query = Assert.Single(_queries);
        Assert.Equal(DayStartUtc, query.From);
        Assert.Equal(DayStartUtc.AddDays(1), query.To);
        Assert.Equal(
            new[] { AnchorOf(C1, ConsumerIn), AnchorOf(C2, ConsumerIn), AnchorOf(P1, ProducerIn) }.Order(StringComparer.Ordinal),
            query.RtIds!.Select(x => x.ToString()));
    }

    [Fact]
    public async Task TwoRuns_ProduceIdenticalOutput()
    {
        Fill(C1, ConsumerIn, i => i * 0.0137);
        Fill(C2, ConsumerIn, i => 0.3 - i * 0.002);
        Fill(P1, ProducerIn, i => i % 7 * 0.0911);

        var first = await RunAsync(Config());
        var second = await RunAsync(Config());

        Assert.Equal(Serialize(first.Records).ToJsonString(), Serialize(second.Records).ToJsonString());
        Assert.Equal(Serialize(first.Summary).ToJsonString(), Serialize(second.Summary).ToJsonString());
    }

    [Fact]
    public async Task MoreDaysThanMaxDays_Throws()
    {
        var config = Config(numDays: 32);
        await Assert.ThrowsAsync<PipelineNodeExecutionException>(() => RunAsync(config));

        var viaPath = Config() with { NumDaysPath = "$.numDays", MaxDays = 5 };
        await Assert.ThrowsAsync<PipelineNodeExecutionException>(() => RunAsync(viaPath,
            dc => A.CallTo(() => dc.GetValue("$.numDays")).Returns(6)));
    }

    [Fact]
    public async Task Paths_OverrideTheLiterals()
    {
        FillDefault(days: 2);
        var config = Config() with
        {
            StartDay = new DateTime(2026, 11, 1),
            NumDays = 5,
            StartDayPath = "$.startDay",
            NumDaysPath = "$.numDays"
        };

        var output = await RunAsync(config, dc =>
        {
            A.CallTo(() => dc.GetValue("$.startDay")).Returns("2026-11-15");
            A.CallTo(() => dc.GetValue("$.numDays")).Returns(2);
        });

        Assert.Equal(["2026-11-15", "2026-11-16"], output.Summary!.Select(s => s.Day));
    }

    [Fact]
    public async Task UnresolvedPaths_FallBackToTheLiterals()
    {
        FillDefault();
        var config = Config() with { StartDayPath = "$.startDay", NumDaysPath = "$.numDays" };

        var output = await RunAsync(config, dc =>
        {
            A.CallTo(() => dc.GetValue("$.startDay")).Returns(null);
            A.CallTo(() => dc.GetValue("$.numDays")).Returns(null);
        });

        Assert.Equal(["2026-11-15"], output.Summary!.Select(s => s.Day));
    }

    [Fact]
    public async Task NoStartDay_AllocatesTheLatestClosedDay()
    {
        FillDefault();
        var config = Config() with { StartDay = null, NumDays = 7 };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero));

        var output = await RunAsync(config, clock: clock);

        Assert.Equal(["2026-11-15"], output.Summary!.Select(s => s.Day));

        var earlier = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 4, 59, 0, TimeSpan.Zero));
        var before = await RunAsync(config, clock: earlier);
        Assert.Equal(["2026-11-14"], before.Summary!.Select(s => s.Day));
    }

    [Fact]
    public async Task CutOffTimePath_ChangesTheCreationTime()
    {
        FillDefault();
        var config = Config() with { CutOffTimePath = "$.cutOff" };

        var output = await RunAsync(config, dc => A.CallTo(() => dc.GetValue("$.cutOff")).Returns("08:30"));

        Assert.All(output.Records!, r => Assert.Equal(new DateTime(2026, 11, 16, 7, 30, 0, DateTimeKind.Utc), r.CreationTime));
    }
}

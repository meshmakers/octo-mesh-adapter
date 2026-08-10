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
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class AggregateStreamDataNodeTests : NodeTestBase
{
    private const string TestTenantId = "test-tenant";
    private static readonly OctoObjectId TestArchiveRtId = new("000000000000000000000042");
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/EnergyMeasurement");
    private static readonly DateTime From = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;
    private readonly IArchiveRuntimeStore _archiveStore;

    private StreamDataAggregationQueryOptions? _capturedPlain;
    private StreamDataGroupedAggregationQueryOptions? _capturedGrouped;
    private StreamDataQueryOptions? _capturedScan;

    public AggregateStreamDataNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();
        _archiveStore = A.Fake<IArchiveRuntimeStore>();

        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _systemContext.FindTenantContextAsync(TestTenantId))
            .Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
        A.CallTo(() => _tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);

        SetupArchive(CreateSnapshot());
        SetupAggregationResult();
        SetupGroupedResult();
        SetupScanResult();
    }

    private AggregateStreamDataNode CreateNode(NodeDelegate next)
        => new(next, _etlContext, _systemContext);

    private static CkArchiveColumnSpec Ingested(string path) => new(path, false, false);

    private static ArchiveSnapshot CreateSnapshot(bool isTimeRange = true,
        TimeSpan? period = null, params CkArchiveColumnSpec[] columns)
        => new(TestArchiveRtId, TestCkTypeId, CkArchiveStatus.Activated, "energy-measurements",
            columns.Length > 0 ? columns : [Ingested("Energy"), Ingested("DataQuality")])
        {
            IsTimeRange = isTimeRange,
            Period = period
        };

    private void SetupArchive(ArchiveSnapshot? snapshot)
        => A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._)).Returns(Task.FromResult(snapshot));

    private void SetupAggregationResult(params StreamDataRow[] rows)
        => A.CallTo(() => _streamDataRepository.ExecuteAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataAggregationQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataAggregationQueryOptions o) => _capturedPlain = o)
            .Returns(Task.FromResult(new StreamDataQueryResult { Rows = rows, TotalCount = rows.Length }));

    private void SetupGroupedResult(params StreamDataRow[] rows)
        => A.CallTo(() => _streamDataRepository.ExecuteGroupedAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataGroupedAggregationQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataGroupedAggregationQueryOptions o) => _capturedGrouped = o)
            .Returns(Task.FromResult(new StreamDataQueryResult { Rows = rows, TotalCount = rows.Length }));

    /// <summary>The coverage scan the gap guard runs — a plain query, not an aggregation.</summary>
    private void SetupScanResult(params StreamDataRow[] rows)
        => A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
                A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataQueryOptions o) => _capturedScan = o)
            .Returns(Task.FromResult(new StreamDataQueryResult { Rows = rows, TotalCount = rows.Length }));

    private static AggregationColumnDto Agg(string path, AggregationTypesDto function)
        => new() { AttributePath = path, Function = function };

    private static AggregateStreamDataNodeConfiguration Config(
        params AggregationColumnDto[] aggregations) => new()
    {
        ArchiveRtId = TestArchiveRtId,
        TargetPath = "$.monthly",
        Aggregations = aggregations.Length > 0
            ? aggregations
            : [Agg("Energy", AggregationTypesDto.Sum)],
        From = From,
        To = To
    };

    private sealed class ResultBox
    {
        public QueryResult? Value { get; set; }
    }

    private static ResultBox CaptureResult(IDataContext dataContext)
    {
        var box = new ResultBox();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is QueryResult qr) box.Value = qr;
            });
        return box;
    }

    /// <summary>A window row for the coverage scan.</summary>
    private static StreamDataRow WindowRow(DateTime start, DateTime end) => new()
    {
        RtId = new OctoObjectId("000000000000000000000001"),
        RtWellKnownName = "METER-A",
        Timestamp = end,
        Values = new Dictionary<string, object?> { ["window_start"] = start }
    };

    // ----------------------------------------------------------- plain aggregation

    [Fact]
    public async Task ProcessObjectAsync_WithoutGroupBy_UsesPlainAggregation()
    {
        SetupAggregationResult(new StreamDataRow
        {
            Values = new Dictionary<string, object?> { ["energy_sum"] = 1234.5 }
        });

        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(_capturedPlain);
        Assert.Null(_capturedGrouped);
        Assert.Equal(TestCkTypeId, _capturedPlain!.CkTypeId);
        Assert.Equal(From, _capturedPlain.From);
        Assert.Equal(To, _capturedPlain.To);

        Assert.Equal(["Energy"], captured.Value!.Columns.Select(x => x.Header));
        var row = Assert.Single(captured.Value.Rows);
        Assert.Equal(1234.5, row.Values[0]);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_MapsEveryFunctionToTheEngineEquivalent()
    {
        var config = Config(
            Agg("Energy", AggregationTypesDto.Sum),
            Agg("DataQuality", AggregationTypesDto.Maximum));
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Collection(_capturedPlain!.AggregationColumns,
            first =>
            {
                Assert.Equal("Energy", first.AttributePath);
                Assert.Equal(AggregationFunction.Sum, first.Function);
            },
            second =>
            {
                Assert.Equal("DataQuality", second.AttributePath);
                Assert.Equal(AggregationFunction.Maximum, second.Function);
            });
    }

    [Fact]
    public async Task ProcessObjectAsync_NoRowsFromStorage_StillEmitsOneRowWithNulls()
    {
        // A downstream consumer always finds the shape it expects.
        SetupAggregationResult();

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var row = Assert.Single(captured.Value!.Rows);
        Assert.Null(row.Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolvesValueViaSqlAliasFallback()
    {
        SetupAggregationResult(new StreamDataRow
        {
            Values = new Dictionary<string, object?> { ["Sum_energy"] = 42.0 }
        });

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(42.0, Assert.Single(captured.Value!.Rows).Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_SamePathTwice_DisambiguatesTheHeaders()
    {
        // The result keys are unique per function, a bare path header would not be.
        SetupAggregationResult(new StreamDataRow
        {
            Values = new Dictionary<string, object?>
            {
                ["energy_min"] = 1.0,
                ["energy_max"] = 9.0
            }
        });

        var config = Config(
            Agg("Energy", AggregationTypesDto.Minimum),
            Agg("Energy", AggregationTypesDto.Maximum));
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Energy (Minimum)", "Energy (Maximum)"],
            captured.Value!.Columns.Select(x => x.Header));
        var row = Assert.Single(captured.Value.Rows);
        Assert.Equal(1.0, row.Values[0]);
        Assert.Equal(9.0, row.Values[1]);
    }

    // --------------------------------------------------------- grouped aggregation

    [Fact]
    public async Task ProcessObjectAsync_WithGroupBy_UsesGroupedAggregation()
    {
        SetupGroupedResult(
            new StreamDataRow
            {
                RtId = new OctoObjectId("000000000000000000000001"),
                Values = new Dictionary<string, object?> { ["rtid"] = "a", ["energy_sum"] = 10.0 }
            },
            new StreamDataRow
            {
                RtId = new OctoObjectId("000000000000000000000002"),
                Values = new Dictionary<string, object?> { ["rtid"] = "b", ["energy_sum"] = 20.0 }
            });

        var config = Config() with { GroupBy = ["rtId"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(_capturedGrouped);
        Assert.Null(_capturedPlain);
        Assert.Equal(["rtId"], _capturedGrouped!.GroupByColumns);

        // Group-by columns first, then the key figures.
        Assert.Equal(["rtId", "Energy"], captured.Value!.Columns.Select(x => x.Header));
        Assert.Equal(2, captured.Value.Rows.Count);
        Assert.Equal(10.0, captured.Value.Rows[0].Values[1]);
        Assert.Equal(20.0, captured.Value.Rows[1].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_GroupByResultHeaderName_IsTranslated()
    {
        var config = Config() with { GroupBy = ["WellKnownName"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["rtWellKnownName"], _capturedGrouped!.GroupByColumns);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownGroupByColumn_Throws()
    {
        // A dropped group-by column would collapse every group into one row and silently change what
        // the figures mean.
        var config = Config() with { GroupBy = ["Nonsense"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Nonsense", ex.Message);
    }

    // ------------------------------------------------------------ filters / scope

    [Fact]
    public async Task ProcessObjectAsync_WellKnownNamesAndRtIds_NarrowTheAggregation()
    {
        var config = Config() with
        {
            WellKnownNames = ["METER-A"],
            RtIds = ["000000000000000000000001"]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedPlain!.FieldFilters!);
        Assert.Equal("rtWellKnownName", filter.AttributePath);
        Assert.Equal(FieldFilterOperator.Equals, filter.Operator);
        Assert.Equal(new OctoObjectId("000000000000000000000001"),
            Assert.Single(_capturedPlain.RtIds!));
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownFilterColumn_Throws()
    {
        var config = Config() with
        {
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "DoesNotExist",
                    Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = 1
                }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_UnspecifiedKindBoundaries_AreReadAsUtc()
    {
        var config = Config() with
        {
            From = DateTime.SpecifyKind(From, DateTimeKind.Unspecified),
            To = DateTime.SpecifyKind(To, DateTimeKind.Unspecified)
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(DateTimeKind.Utc, _capturedPlain!.From!.Value.Kind);
        Assert.Equal(From, _capturedPlain.From);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvertedTimeRange_Throws()
    {
        var config = Config() with { From = To, To = From };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    // -------------------------------------------------------------- gap guard

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFreeAndComplete_Aggregates()
    {
        SetupArchive(CreateSnapshot(period: TimeSpan.FromMinutes(15)));
        SetupScanResult(WindowRow(From, To));

        var config = Config() with { RequireGapFree = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Coverage scan first, then the aggregation.
        Assert.NotNull(_capturedScan);
        Assert.Equal(["window_start"], _capturedScan!.Columns);
        Assert.NotNull(_capturedPlain);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFreeWithGap_ThrowsNamingTheSeries()
    {
        SetupArchive(CreateSnapshot(period: TimeSpan.FromMinutes(15)));
        // Covers only the first hour of the month.
        SetupScanResult(WindowRow(From, From.AddHours(1)));

        var config = Config() with { RequireGapFree = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("METER-A", ex.Message);
        Assert.Contains("missing", ex.Message);
        // No partial figure: the aggregation never ran.
        Assert.Null(_capturedPlain);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithoutRequireGapFree_DoesNotScan()
    {
        SetupScanResult(WindowRow(From, From.AddHours(1)));

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Gaps are irrelevant unless the guard is on — and no extra query is spent on them.
        Assert.Null(_capturedScan);
        Assert.NotNull(_capturedPlain);
    }

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFreeWithOverlapOnly_AggregatesButWarns()
    {
        SetupArchive(CreateSnapshot(period: TimeSpan.FromMinutes(15)));
        // Full coverage, but one window is contained in another.
        SetupScanResult(WindowRow(From, To), WindowRow(From, From.AddHours(1)));

        var config = Config() with { RequireGapFree = true };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Overlaps are legal per the storage concept, so the guard passes — but they double-count.
        Assert.NotNull(_capturedPlain);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("overlapping windows"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFreeOnRawArchive_Throws()
    {
        SetupArchive(CreateSnapshot(isTimeRange: false));

        var config = Config() with { RequireGapFree = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("raw archive", ex.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProcessObjectAsync_RequireGapFreeWithoutBothBoundaries_Throws(bool withFrom,
        bool withTo)
    {
        var config = Config() with
        {
            RequireGapFree = true,
            From = withFrom ? From : null,
            To = withTo ? To : null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_NonPositiveExpectedInterval_Throws()
    {
        var config = Config() with { RequireGapFree = true, ExpectedInterval = TimeSpan.Zero };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("ExpectedInterval", ex.Message);
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public async Task ProcessObjectAsync_NoAggregations_Throws()
    {
        var config = new AggregateStreamDataNodeConfiguration
        {
            ArchiveRtId = TestArchiveRtId,
            TargetPath = "$.monthly",
            Aggregations = []
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData(AggregationTypesDto.None)]
    [InlineData(AggregationTypesDto.TimeWeightedAverage)]
    [InlineData(AggregationTypesDto.StateDuration)]
    public async Task ProcessObjectAsync_UnsupportedFunction_ThrowsPointingAtGetQueryById(
        AggregationTypesDto function)
    {
        var config = Config(Agg("Energy", function));
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("GetQueryById", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownAggregationColumn_Throws()
    {
        var config = Config(Agg("NotAColumn", AggregationTypesDto.Sum));
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("NotAColumn", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_StreamDataNotEnabled_Throws()
    {
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(null);

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_ArchiveNotFound_Throws()
    {
        SetupArchive(null);

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_StorageFailure_IsWrapped()
    {
        A.CallTo(() => _streamDataRepository.ExecuteAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataAggregationQueryOptions>._))
            .ThrowsAsync(new InvalidOperationException("crate exploded"));

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("crate exploded", ex.Message);
    }
}

using System.Text.Json.Nodes;
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

public class GetStreamDataNodeTests : NodeTestBase
{
    private const string TestTenantId = "test-tenant";
    private static readonly OctoObjectId TestArchiveRtId = new("000000000000000000000042");
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/SensorReading");

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;
    private readonly IArchiveRuntimeStore _archiveStore;

    private StreamDataQueryOptions? _capturedOptions;
    private OctoObjectId? _capturedArchiveRtId;

    public GetStreamDataNodeTests()
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
        SetupQueryResult();
    }

    private GetStreamDataNode CreateNode(NodeDelegate next)
        => new(next, _etlContext, _systemContext);

    /// <summary>
    /// The columns most tests project. The field resolver validates names against the snapshot, so an
    /// archive without columns would reject every projection.
    /// </summary>
    private static readonly CkArchiveColumnSpec[] DefaultColumns =
        [Ingested("Temperature"), Ingested("Amount.Value"), Ingested("Energy")];

    private static ArchiveSnapshot CreateSnapshot(
        bool isTimeRange = false, params CkArchiveColumnSpec[] columns)
        => new(TestArchiveRtId, TestCkTypeId, CkArchiveStatus.Activated, "test-archive",
            columns.Length > 0 ? columns : DefaultColumns)
        {
            IsTimeRange = isTimeRange
        };

    /// <summary>An archive that declares no data columns at all — only the standard ones exist.</summary>
    private static ArchiveSnapshot EmptySnapshot()
        => new(TestArchiveRtId, TestCkTypeId, CkArchiveStatus.Activated, "test-archive", []);

    private static CkArchiveColumnSpec Ingested(string path)
        => new(path, Indexed: false, Required: false);

    /// <summary>A formula column: empty Path, addressed by Name.</summary>
    private static CkArchiveColumnSpec Computed(string name, int version = 0)
        => new(string.Empty, Indexed: false, Required: false)
        {
            Name = name,
            Formula = "a + b",
            ComputedVersion = version
        };

    private void SetupArchive(ArchiveSnapshot? snapshot)
    {
        A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._))
            .Returns(Task.FromResult(snapshot));
    }

    private void SetupQueryResult(params StreamDataRow[] rows)
    {
        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
                A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .Invokes((OctoObjectId archiveRtId, StreamDataQueryOptions options) =>
            {
                _capturedArchiveRtId = archiveRtId;
                _capturedOptions = options;
            })
            .Returns(Task.FromResult(new StreamDataQueryResult
            {
                Rows = rows,
                TotalCount = rows.Length
            }));
    }

    private static GetStreamDataNodeConfiguration Config(
        Action<GetStreamDataNodeConfiguration>? _ = null)
        => new() { ArchiveRtId = TestArchiveRtId, TargetPath = "$.result" };

    /// <summary>
    /// Holder for the result the node writes. Needed because the capture happens when the node runs,
    /// long after this helper returns — a by-value return would always be null.
    /// </summary>
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

    // ---------------------------------------------------------------- basics

    [Fact]
    public async Task ProcessObjectAsync_WritesResultAndCallsNext()
    {
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedArchiveRtId);
        A.CallTo(() => dataContext.Set("$.result", A<QueryResult?>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PassesCkTypeIdFromArchiveSnapshot()
    {
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestCkTypeId, _capturedOptions!.CkTypeId);
    }

    [Fact]
    public async Task ProcessObjectAsync_PassesColumnsSkipTakeAndLimit()
    {
        var config = Config() with
        {
            Columns = ["Temperature", "Amount.Value"],
            Skip = 5,
            Take = 10,
            Limit = 100
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature", "Amount.Value"], _capturedOptions!.Columns);
        Assert.Equal(5, _capturedOptions.Offset);
        Assert.Equal(10, _capturedOptions.PageSize);
        Assert.Equal(100, _capturedOptions.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_MapsSortOrders()
    {
        var config = Config() with
        {
            SortOrders =
            [
                new SortOrderDto { AttributeName = "timestamp", SortOrder = SortOrdersDto.Descending }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var sortOrder = Assert.Single(_capturedOptions!.SortOrders!);
        Assert.Equal("timestamp", sortOrder.AttributePath);
        Assert.Equal(SortOrders.Descending, sortOrder.SortOrder);
    }

    // ------------------------------------------ sorting / filtering by result-header names

    [Fact]
    public async Task ProcessObjectAsync_SortByWindowStart_TranslatesToPhysicalColumn()
    {
        // The reported bug: "WindowStart" is the result header, but the storage resolver only knows
        // "window_start" and drops anything else without a word — the rows came back unordered.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "WindowStart", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var sort = Assert.Single(_capturedOptions!.SortOrders!);
        Assert.Equal("window_start", sort.AttributePath);
        Assert.Equal(SortOrders.Ascending, sort.SortOrder);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByTimestampOnWindowedArchive_UsesWindowEnd()
    {
        // A windowed archive has no timestamp column — its time axis is window_end. Passing
        // "timestamp" through unchanged would be dropped just as silently.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "Timestamp", SortOrder = SortOrdersDto.Descending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("window_end", Assert.Single(_capturedOptions!.SortOrders!).AttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByTimestampOnRawArchive_UsesTimestamp()
    {
        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "Timestamp", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("timestamp", Assert.Single(_capturedOptions!.SortOrders!).AttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByWellKnownName_TranslatesToPhysicalColumn()
    {
        var config = Config() with
        {
            SortOrders =
                [new SortOrderDto { AttributeName = "WellKnownName", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("rtWellKnownName", Assert.Single(_capturedOptions!.SortOrders!).AttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByArchiveColumn_PassesThroughUnchanged()
    {
        SetupArchive(CreateSnapshot(false, Ingested("Amount.Value")));

        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "Amount.Value", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("Amount.Value", Assert.Single(_capturedOptions!.SortOrders!).AttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByUnknownColumn_Throws()
    {
        // Better a loud failure than silently unordered rows.
        SetupArchive(CreateSnapshot(false, Ingested("Temperature")));

        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "Temparatur", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Temparatur", ex.Message);
        // The message must name what the caller could have written instead.
        Assert.Contains("Temperature", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByWindowStartOnRawArchive_Throws()
    {
        // A raw archive has no window — accepting the name would produce an unordered result.
        var config = Config() with
        {
            SortOrders = [new SortOrderDto { AttributeName = "WindowStart", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_FieldFilterOnResultHeader_TranslatesToPhysicalColumn()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = Config() with
        {
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "WindowStart",
                    Operator = FieldFilterOperatorDto.GreaterThan,
                    ComparisonValue = "2026-07-01T00:00:00Z"
                }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedOptions!.FieldFilters!);
        Assert.Equal("window_start", filter.AttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_FieldFilterOnUnknownColumn_Throws()
    {
        // An ignored filter widens the result instead of narrowing it — worse than an ignored sort.
        SetupArchive(CreateSnapshot(false, Ingested("Temperature")));

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

    // ------------------------------------------------------- wellKnownName

    [Fact]
    public async Task ProcessObjectAsync_SingleWellKnownName_UsesEqualsFilter()
    {
        var config = Config() with { WellKnownNames = ["METER-4711"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedOptions!.FieldFilters!);
        Assert.Equal("rtWellKnownName", filter.AttributePath);
        Assert.Equal(FieldFilterOperator.Equals, filter.Operator);
        Assert.Equal("METER-4711", filter.ComparisonValue);
    }

    [Fact]
    public async Task ProcessObjectAsync_MultipleWellKnownNames_UsesInFilter()
    {
        var config = Config() with { WellKnownNames = ["METER-4711", "METER-4712"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedOptions!.FieldFilters!);
        Assert.Equal("rtWellKnownName", filter.AttributePath);
        Assert.Equal(FieldFilterOperator.In, filter.Operator);
    }

    [Fact]
    public async Task ProcessObjectAsync_WellKnownNamesPath_ReadsListFromPipelineData()
    {
        var config = Config() with { WellKnownNamesPath = "$.meters" };
        var testData = new JsonObject
        {
            ["meters"] = new JsonArray("METER-1", "METER-2")
        };
        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        SetupMatches(dataContext, "$.meters", new JsonArray("METER-1", "METER-2"));

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedOptions!.FieldFilters!);
        Assert.Equal(FieldFilterOperator.In, filter.Operator);
    }

    [Fact]
    public async Task ProcessObjectAsync_LiteralWellKnownNamesWinOverPath()
    {
        var config = Config() with
        {
            WellKnownNames = ["LITERAL"],
            WellKnownNamesPath = "$.meters"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filter = Assert.Single(_capturedOptions!.FieldFilters!);
        Assert.Equal("LITERAL", filter.ComparisonValue);
        // The path must not even be looked at when the literal is configured.
        A.CallTo(() => dataContext.SelectMatches("$.meters")).MustNotHaveHappened();
    }

    // --------------------------------------------------------------- rtIds

    [Fact]
    public async Task ProcessObjectAsync_RtIds_AreScopedOnTheQuery()
    {
        var config = Config() with { RtIds = ["000000000000000000000001"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var rtId = Assert.Single(_capturedOptions!.RtIds!);
        Assert.Equal(new OctoObjectId("000000000000000000000001"), rtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidRtId_Throws()
    {
        var config = Config() with { RtIds = ["not-an-object-id"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    // ----------------------------------------------------------- time range

    [Fact]
    public async Task ProcessObjectAsync_UnspecifiedKindBoundaries_AreReadAsUtc()
    {
        // JSON such as "2026-07-01T00:00:00" deserialises to Kind=Unspecified; the storage layer would
        // otherwise read it as the adapter host's local time (AB#4734).
        var from = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var config = Config() with { From = from, To = to };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(DateTimeKind.Utc, _capturedOptions!.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedOptions.To!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), _capturedOptions.From);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), _capturedOptions.To);
    }

    [Fact]
    public async Task ProcessObjectAsync_OneSidedTimeRange_IsHonoured()
    {
        var config = Config() with { From = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(_capturedOptions!.From);
        Assert.Null(_capturedOptions.To);
    }

    [Fact]
    public async Task ProcessObjectAsync_FromPath_ReadsBoundaryFromPipelineData()
    {
        var config = Config() with { FromPath = "$.range.from" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetValue("$.range.from", A<bool>._))
            .Returns("2026-07-01T00:00:00Z");

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), _capturedOptions!.From);
    }

    [Fact]
    public async Task ProcessObjectAsync_LiteralFromWinsOverFromPath()
    {
        var literal = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var config = Config() with { From = literal, FromPath = "$.range.from" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetValue("$.range.from", A<bool>._))
            .Returns("2026-07-01T00:00:00Z");

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(literal, _capturedOptions!.From);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnresolvedFromPath_WarnsAndLeavesBoundaryOpen()
    {
        var config = Config() with { FromPath = "$.range.from" };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        A.CallTo(() => dataContext.GetValue("$.range.from", A<bool>._)).Returns(null);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Null(_capturedOptions!.From);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("resolved to no value"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NonDateValueAtFromPath_Throws()
    {
        var config = Config() with { FromPath = "$.range.from" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetValue("$.range.from", A<bool>._)).Returns("not-a-date");

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_InvertedTimeRange_Throws()
    {
        var config = Config() with
        {
            From = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProcessObjectAsync_NonPositiveLimit_Throws(int limit)
    {
        var config = Config() with { Limit = limit };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    // ------------------------------------------------------------- failures

    [Fact]
    public async Task ProcessObjectAsync_StreamDataNotEnabled_Throws()
    {
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(null);
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_ArchiveNotFound_Throws()
    {
        SetupArchive(null);
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_StorageFailure_IsWrapped()
    {
        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
                A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .ThrowsAsync(new InvalidOperationException("crate exploded"));
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("crate exploded", ex.Message);
    }

    // -------------------------------------------------------- result shape

    [Fact]
    public async Task ProcessObjectAsync_RawArchive_EmitsTimestampThenColumns()
    {
        SetupQueryResult(new StreamDataRow
        {
            RtId = new OctoObjectId("000000000000000000000001"),
            CkTypeId = TestCkTypeId,
            Timestamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?> { ["amountvalue"] = 42.0 }
        });

        var config = Config() with { Columns = ["Amount.Value"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        Assert.Equal(["Timestamp", "Amount.Value"], captured.Value!.Columns.Select(x => x.Header));
        var row = Assert.Single(captured.Value!.Rows);
        // The dotted path resolves through the physical column name (dots stripped, lower-cased).
        Assert.Equal(42.0, row.Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WindowedArchive_ProjectsAndEmitsWindowColumns()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true));
        SetupQueryResult(new StreamDataRow
        {
            Timestamp = new DateTime(2026, 7, 1, 0, 15, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?>
            {
                ["window_start"] = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                ["window_end"] = new DateTime(2026, 7, 1, 0, 15, 0, DateTimeKind.Utc),
                ["energy"] = 7.5
            }
        });

        var config = Config() with { Columns = ["Energy"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // window_start must be requested explicitly, otherwise it never reaches StreamDataRow.Values.
        Assert.Contains("window_start", _capturedOptions!.Columns);
        Assert.Contains("window_end", _capturedOptions.Columns);

        Assert.Equal(["Timestamp", "WindowStart", "WindowEnd", "Energy"],
            captured.Value!.Columns.Select(x => x.Header));
        var row = Assert.Single(captured.Value!.Rows);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), row.Values[1]);
        Assert.Equal(7.5, row.Values[3]);
    }

    [Fact]
    public async Task ProcessObjectAsync_RawArchive_DoesNotRequestWindowColumns()
    {
        var config = Config() with { Columns = ["Temperature"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature"], _capturedOptions!.Columns);
    }

    // ------------------------------------------- no columns configured = read the whole archive

    [Fact]
    public async Task ProcessObjectAsync_NoColumns_ProjectsEveryIngestedArchiveColumn()
    {
        SetupArchive(CreateSnapshot(false, Ingested("Temperature"), Ingested("Amount.Value")));
        SetupQueryResult(new StreamDataRow
        {
            Timestamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            RtWellKnownName = "Sensor001",
            Values = new Dictionary<string, object?>
            {
                ["temperature"] = 20.0,
                ["amountvalue"] = 42.0
            }
        });

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature", "Amount.Value"], _capturedOptions!.Columns);
        Assert.Equal(["Timestamp", "WellKnownName", "Temperature", "Amount.Value"],
            captured.Value!.Columns.Select(x => x.Header));

        var row = Assert.Single(captured.Value!.Rows);
        Assert.Equal("Sensor001", row.Values[1]);
        Assert.Equal(20.0, row.Values[2]);
        Assert.Equal(42.0, row.Values[3]);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoColumnsOnWindowedArchive_EmitsWindowThenWellKnownName()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(new StreamDataRow
        {
            Timestamp = new DateTime(2026, 7, 1, 0, 15, 0, DateTimeKind.Utc),
            RtWellKnownName = "METER-4711",
            Values = new Dictionary<string, object?>
            {
                ["window_start"] = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                ["window_end"] = new DateTime(2026, 7, 1, 0, 15, 0, DateTimeKind.Utc),
                ["energy"] = 7.5
            }
        });

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Timestamp", "WindowStart", "WindowEnd", "WellKnownName", "Energy"],
            captured.Value!.Columns.Select(x => x.Header));

        var row = Assert.Single(captured.Value!.Rows);
        Assert.Equal("METER-4711", row.Values[3]);
        Assert.Equal(7.5, row.Values[4]);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoColumns_IncludesComputedColumns()
    {
        // Computed columns used to be skipped because their storage key could not be derived; the
        // field resolver supplies it now, so reading the whole archive includes them (AB#4764).
        SetupArchive(CreateSnapshot(false, Ingested("Temperature"), Computed("Derived")));

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature", "Derived"], _capturedOptions!.Columns);
        Assert.Contains("Derived", captured.Value!.Columns.Select(x => x.Header));
        // A computed column carries no Path, so an empty name must never reach the query.
        Assert.DoesNotContain(string.Empty, _capturedOptions.Columns);
    }

    [Fact]
    public async Task ProcessObjectAsync_VersionedComputedColumn_ReadsItsValue()
    {
        // The bug itself (AB#4764): after a formula change the physical column is {base}__v{N}, which
        // no derivation from the name reproduces. Deriving it yielded null for every row.
        SetupArchive(CreateSnapshot(false, Computed("Derived", version: 2)));
        SetupQueryResult(new StreamDataRow
        {
            Timestamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?> { ["derived__v2"] = 99.5 }
        });

        var config = Config() with { Columns = ["Derived"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // The query asks by logical name, the value is read by the versioned storage key.
        Assert.Equal(["Derived"], _capturedOptions!.Columns);
        Assert.Equal(["Timestamp", "Derived"], captured.Value!.Columns.Select(x => x.Header));
        Assert.Equal(99.5, Assert.Single(captured.Value.Rows).Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_ComputedColumnMidBackfill_IsNotProjected()
    {
        // The read path hides a column whose backfill has not committed; the resolver does not
        // register it, so it cannot leak into the automatic set either.
        var pending = new CkArchiveColumnSpec(string.Empty, false, false)
        {
            Name = "Pending",
            Formula = "a + b",
            ComputedState = ComputedColumnState.Backfilling
        };
        SetupArchive(CreateSnapshot(false, Ingested("Temperature"), pending));

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature"], _capturedOptions!.Columns);
    }

    [Fact]
    public async Task ProcessObjectAsync_ArchiveWithoutColumns_StillReturnsTimeAxisAndName()
    {
        SetupArchive(EmptySnapshot());
        SetupQueryResult(new StreamDataRow
        {
            Timestamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            RtWellKnownName = "Sensor001"
        });

        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(_capturedOptions!.Columns);
        Assert.Equal(["Timestamp", "WellKnownName"], captured.Value!.Columns.Select(x => x.Header));
    }

    [Fact]
    public async Task ProcessObjectAsync_ExplicitColumns_DoNotGainWellKnownName()
    {
        // The caller's list is honoured as given; rtWellKnownName can be named there like any column.
        SetupArchive(CreateSnapshot(false, Ingested("Temperature"), Ingested("Amount.Value")));

        var config = Config() with { Columns = ["Temperature"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature"], _capturedOptions!.Columns);
        Assert.Equal(["Timestamp", "Temperature"], captured.Value!.Columns.Select(x => x.Header));
    }

    [Fact]
    public async Task ProcessObjectAsync_BlankConfiguredColumns_CountAsUnset()
    {
        SetupArchive(CreateSnapshot(false, Ingested("Temperature")));

        var config = Config() with { Columns = ["  ", ""] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var captured = CaptureResult(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["Temperature"], _capturedOptions!.Columns);
        Assert.Contains("WellKnownName", captured.Value!.Columns.Select(x => x.Header));
    }

    // ------------------------------------------------------------- gap detection

    private static readonly DateTime GapFrom = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GapTo = new(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc);

    private GetStreamDataNodeConfiguration GapConfig() => Config() with
    {
        From = GapFrom,
        To = GapTo,
        GapsTargetPath = "$.gaps"
    };

    private static StreamDataRow WindowRow(int startMin, int endMin, string wellKnownName = "METER-A")
        => new()
        {
            RtId = new OctoObjectId("000000000000000000000001"),
            RtWellKnownName = wellKnownName,
            Timestamp = GapFrom.AddMinutes(endMin),
            Values = new Dictionary<string, object?>
            {
                ["window_start"] = GapFrom.AddMinutes(startMin)
            }
        };

    private sealed class GapBox
    {
        public StreamDataGapReport? Value { get; set; }
    }

    private static GapBox CaptureGaps(IDataContext dataContext)
    {
        var box = new GapBox();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is StreamDataGapReport report) box.Value = report;
            });
        return box;
    }

    [Fact]
    public async Task ProcessObjectAsync_GapsTargetPath_WritesReportAndRunsSecondQuery()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(WindowRow(0, 15), WindowRow(15, 30), WindowRow(45, 60));

        var (dataContext, nodeContext, next) = PrepareTest(GapConfig());
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Data query plus a dedicated coverage scan — the data query's paging would hide rows.
        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
            A<OctoObjectId>._, A<StreamDataQueryOptions>._)).MustHaveHappenedTwiceExactly();

        Assert.NotNull(gaps.Value);
        var series = Assert.Single(gaps.Value!.Series);
        var gap = Assert.Single(series.Gaps);
        Assert.Equal(GapFrom.AddMinutes(30), gap.From);
        Assert.Equal(GapFrom.AddMinutes(45), gap.To);
        A.CallTo(() => dataContext.Set("$.gaps", A<StreamDataGapReport?>._,
            A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapScan_RequestsOnlyTheWindowStart()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var (dataContext, nodeContext, next) = PrepareTest(GapConfig() with { GapsOnly = true });

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // window_end arrives as the row timestamp and the name sits on the row — neither is projected.
        Assert.Equal(["window_start"], _capturedOptions!.Columns);
        Assert.Null(_capturedOptions.Offset);
        Assert.Null(_capturedOptions.PageSize);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapsOnly_SkipsTheDataQuery()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(WindowRow(0, 60));

        var (dataContext, nodeContext, next) = PrepareTest(GapConfig() with { GapsOnly = true });

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
            A<OctoObjectId>._, A<StreamDataQueryOptions>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set("$.result", A<QueryResult?>._,
            A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_GapsOnly_DoesNotValidateSortColumns()
    {
        // The sort belongs to a query that never runs; failing on it would only confuse.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = GapConfig() with
        {
            GapsOnly = true,
            SortOrders = [new SortOrderDto { AttributeName = "Nonsense", SortOrder = SortOrdersDto.Ascending }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapScan_UsesArchivePeriodAsInterval()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy"))
            with { Period = TimeSpan.FromMinutes(15) });
        SetupQueryResult(WindowRow(0, 15));

        var (dataContext, nodeContext, next) = PrepareTest(GapConfig() with { GapsOnly = true });
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("PT15M", gaps.Value!.Interval);
        Assert.Equal(4, gaps.Value.Series[0].ExpectedIntervals);
    }

    [Fact]
    public async Task ProcessObjectAsync_ExpectedInterval_WinsOverArchivePeriod()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy"))
            with { Period = TimeSpan.FromMinutes(15) });
        SetupQueryResult(WindowRow(0, 15));

        var config = GapConfig() with { GapsOnly = true, ExpectedInterval = TimeSpan.FromMinutes(30) };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("PT30M", gaps.Value!.Interval);
        Assert.Equal(2, gaps.Value.Series[0].ExpectedIntervals);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoIntervalAnywhere_WarnsAndOmitsCounts()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(WindowRow(0, 15));

        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger(GapConfig() with { GapsOnly = true });
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Null(gaps.Value!.Interval);
        Assert.Null(gaps.Value.Series[0].ExpectedIntervals);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("No interval known"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_OverlappingWindows_AreWarnedAboutNotFailed()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(WindowRow(0, 60), WindowRow(0, 30));

        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger(GapConfig() with { GapsOnly = true });
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.True(gaps.Value!.Series[0].HasOverlaps);
        Assert.True(gaps.Value.IsComplete);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("overlapping windows"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ConfiguredRtIdWithoutRows_BecomesAFullGap()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        // Nothing at all came back for the requested entity.
        SetupQueryResult();

        var config = GapConfig() with { GapsOnly = true, RtIds = ["000000000000000000000009"] };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var series = Assert.Single(gaps.Value!.Series);
        Assert.Equal(new OctoObjectId("000000000000000000000009"), series.RtId);
        var gap = Assert.Single(series.Gaps);
        Assert.Equal(GapFrom, gap.From);
        Assert.Equal(GapTo, gap.To);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapScanRowCapExceeded_Throws()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        // Cap of 1 means the scan asks for 2; returning 2 proves it was truncated.
        SetupQueryResult(WindowRow(0, 15), WindowRow(15, 30));

        var config = GapConfig() with { GapsOnly = true, MaxGapScanRows = 1 };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("row cap", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_MaxGapScanRowsAtIntMaxValue_DoesNotOverflowTheLimit()
    {
        // int.MaxValue is the natural way to ask for "no cap"; maxRows + 1 would wrap negative and
        // reach the storage layer as an invalid limit.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));
        SetupQueryResult(WindowRow(0, 60));

        var config = GapConfig() with { GapsOnly = true, MaxGapScanRows = int.MaxValue };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(int.MaxValue, _capturedOptions!.Limit);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProcessObjectAsync_NonPositiveMaxGapScanRows_Throws(int maxRows)
    {
        // Silently falling back to the default would hide that the configured value means nothing.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = GapConfig() with { GapsOnly = true, MaxGapScanRows = maxRows };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("MaxGapScanRows", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_ZeroExpectedInterval_Throws()
    {
        // Treating it as "none configured" would warn about an unset property the author did set.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = GapConfig() with { GapsOnly = true, ExpectedInterval = TimeSpan.Zero };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("ExpectedInterval", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_ArchivePeriodOfZero_ReportsRangesWithoutCounts()
    {
        // The remaining path to "no interval": nothing configured and the archive declares none.
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy"))
            with { Period = TimeSpan.Zero });
        SetupQueryResult(WindowRow(0, 15));

        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger(GapConfig() with { GapsOnly = true });
        var gaps = CaptureGaps(dataContext);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Interval and counts agree: both absent, never "PT0S" next to empty counts.
        Assert.Null(gaps.Value!.Interval);
        Assert.Null(gaps.Value.Series[0].ExpectedIntervals);
        Assert.NotEmpty(gaps.Value.Series[0].Gaps);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("No interval known"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_GapsOnRawArchive_Throws()
    {
        // A raw archive stores single timestamps — there is no interval coverage to judge.
        SetupArchive(CreateSnapshot(columns: Ingested("Temperature")));

        var (dataContext, nodeContext, next) = PrepareTest(GapConfig());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("raw archive", ex.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProcessObjectAsync_GapsWithoutBothBoundaries_Throws(bool withFrom, bool withTo)
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = Config() with
        {
            GapsTargetPath = "$.gaps",
            From = withFrom ? GapFrom : null,
            To = withTo ? GapTo : null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_GapsOnlyWithoutTargetPath_Throws()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var config = Config() with { GapsOnly = true, From = GapFrom, To = GapTo };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithoutGapsTargetPath_RunsOnlyTheDataQuery()
    {
        SetupArchive(CreateSnapshot(isTimeRange: true, columns: Ingested("Energy")));

        var (dataContext, nodeContext, next) = PrepareTest(Config());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
            A<OctoObjectId>._, A<StreamDataQueryOptions>._)).MustHaveHappenedOnceExactly();
    }

    private static void SetupMatches(IDataContext dataContext, string path, JsonArray array)
    {
        var match = A.Fake<IDataContext>();
        A.CallTo(() => match.GetKind("$")).Returns(DataKind.Array);
        A.CallTo(() => match.Length("$")).Returns(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            A.CallTo(() => match.GetValue($"$[{i}]", A<bool>._))
                .Returns(array[i]!.GetValue<string>());
        }

        A.CallTo(() => dataContext.SelectMatches(path)).Returns(new[] { match });
    }
}

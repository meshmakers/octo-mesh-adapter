using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetQueryByIdNodeTests : NodeTestBase
{
    private static readonly OctoObjectId TestQueryRtId = new("000000000000000000000099");
    private static readonly CkId<CkTypeId> TestCkTypeId = new("TestModel", new CkTypeId("TestType-1"));
    private const string TestTenantId = "test-tenant";

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;
    private readonly ICkCacheService _ckCacheService;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;

    public GetQueryByIdNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();
        _ckCacheService = A.Fake<ICkCacheService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _tenantRepository.TenantId).Returns(TestTenantId);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));

        A.CallTo(() => _systemContext.FindTenantContextAsync(TestTenantId))
            .Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);

        var ckTypeDto = new CkCompiledTypeDto { TypeId = new CkTypeId("TestType-1") };
        var ckTypeGraph = new CkTypeGraph(TestCkTypeId, ckTypeDto);
        A.CallTo(() => _ckCacheService.GetRtCkType(TestTenantId, A<RtCkId<CkTypeId>>._))
            .Returns(ckTypeGraph);
        A.CallTo(() => _ckCacheService.TryGetCkType(TestTenantId, A<CkId<CkTypeId>>._, out ckTypeGraph))
            .Returns(true)
            .AssignsOutAndRefParameters(ckTypeGraph);
    }

    private GetQueryByIdNode CreateNode(NodeDelegate next)
    {
        return new GetQueryByIdNode(next, _etlContext, _ckCacheService, _systemContext);
    }

    private void SetupQueryEntityNotFound()
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(null));
    }

    private void SetupSimpleQuery(RtSimpleRtQuery simpleQuery)
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(simpleQuery));
    }

    private void SetupAggregationQuery(RtAggregationRtQuery aggregationQuery)
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(aggregationQuery));
    }

    private void SetupGroupingAggregationQuery(RtGroupingAggregationRtQuery groupedQuery)
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(groupedQuery));
    }

    private void SetupGraphByTypeResult(IResultSet<RtEntityGraphItem> resultSet)
    {
        A.CallTo(() => _tenantRepository.GetRtEntitiesGraphByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<ICollection<NavigationPair>>._,
                A<int?>._,
                A<int?>._))
            .Returns(resultSet);
    }

    private static IResultSet<RtEntityGraphItem> CreateEmptyGraphResultSet(
        AggregationResult? aggregationResult = null,
        IEnumerable<FieldAggregationResult>? fieldAggregationResult = null)
    {
        var resultSet = A.Fake<IResultSet<RtEntityGraphItem>>();
        A.CallTo(() => resultSet.Items).Returns([]);
        A.CallTo(() => resultSet.TotalCount).Returns(0);
        A.CallTo(() => resultSet.AggregationResult).Returns(aggregationResult);
        A.CallTo(() => resultSet.FieldAggregationResult).Returns(fieldAggregationResult);
        return resultSet;
    }

    private static GetQueryByIdNodeConfiguration CreateConfig(
        int? skip = null, int? take = null)
    {
        return new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            Skip = skip,
            Take = take
        };
    }

    private static void CaptureSetCall(IDataContext dataContext, string targetPath,
        Action<QueryResult?> capture)
    {
        A.CallTo(() => dataContext.Set(
                targetPath,
                A<QueryResult?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, QueryResult? qr, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capture(qr));
    }

    #region Query Not Found

    [Fact]
    public async Task ProcessObjectAsync_WithNonExistentQuery_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupQueryEntityNotFound();

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    #endregion

    #region Simple Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var simpleQuery = new RtSimpleRtQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            Columns = new AttributeStringValueList(["col1", "col2"])
        };
        SetupSimpleQuery(simpleQuery);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleQuery_PassesSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var simpleQuery = new RtSimpleRtQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            Columns = new AttributeStringValueList(["col1"])
        };
        SetupSimpleQuery(simpleQuery);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesGraphByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<ICollection<NavigationPair>>._,
                5,
                10))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleQuery_SetsQueryResultOnDataContext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var simpleQuery = new RtSimpleRtQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            Columns = new AttributeStringValueList(["col1"])
        };
        SetupSimpleQuery(simpleQuery);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.queryResult",
                A<QueryResult?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    #endregion

    #region Aggregation Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 42.5 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_PassesNullSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 0 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesGraphByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<ICollection<NavigationPair>>._,
                null,
                null))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_SetsSingleRowResult()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 42.5 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Single(capturedResult!.Columns);
        Assert.Equal("quantity", capturedResult.Columns[0].Header);
        Assert.Single(capturedResult.Rows);
        Assert.Single(capturedResult.Rows[0].Values);
        Assert.Equal(42.5, capturedResult.Rows[0].Values[0]);
        Assert.Null(capturedResult.Rows[0].RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_NullResult_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: null));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_MultipleColumns_ReturnsAllValues()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = new RtAggregationRtQuery { QueryCkTypeId = "TestModel/TestType" };
        var col1 = new RtAggregationQueryColumnRecord
        {
            AttributePath = "quantity",
            AggregationType = RtAggregationTypesEnum.Sum
        };
        var col2 = new RtAggregationQueryColumnRecord
        {
            AttributePath = "price",
            AggregationType = RtAggregationTypesEnum.Average
        };
        var col3 = new RtAggregationQueryColumnRecord
        {
            AttributePath = "quantity",
            AggregationType = RtAggregationTypesEnum.Count
        };
        aggregationQuery.Columns.Add(col1);
        aggregationQuery.Columns.Add(col2);
        aggregationQuery.Columns.Add(col3);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 100.0 }],
            avgStats: [new StatisticsResult { AttributePath = "price", Value = 25.5 }],
            countStats: [new StatisticsResult { AttributePath = "quantity", Value = 4L }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(3, capturedResult!.Columns.Count);
        Assert.Single(capturedResult.Rows);
        Assert.Equal(3, capturedResult.Rows[0].Values.Count);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[0]);
        Assert.Equal(25.5, capturedResult.Rows[0].Values[1]);
        Assert.Equal(4L, capturedResult.Rows[0].Values[2]);
    }

    #endregion

    #region Grouped Aggregation Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        var fieldAggResults = new[]
        {
            CreateFieldAggregationResult(["status"], ["Active"], 1,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 100.0 }])
        };
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: fieldAggResults));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_PassesNullSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        var fieldAggResults = new[]
        {
            CreateFieldAggregationResult(["status"], ["Active"], 1,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 0 }])
        };
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: fieldAggResults));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesGraphByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<ICollection<NavigationPair>>._,
                null,
                null))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_BuildsColumnsAndRows()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        var fieldAggResults = new[]
        {
            CreateFieldAggregationResult(["status"], ["Active"], 3,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 100.0 }]),
            CreateFieldAggregationResult(["status"], ["Inactive"], 2,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 50.0 }])
        };
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: fieldAggResults));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);

        Assert.Equal(2, capturedResult!.Columns.Count);
        Assert.Equal("status", capturedResult.Columns[0].Header);
        Assert.Equal("quantity", capturedResult.Columns[1].Header);

        Assert.Equal(2, capturedResult.Rows.Count);

        Assert.Equal("Active", capturedResult.Rows[0].Values[0]);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[1]);
        Assert.Null(capturedResult.Rows[0].RtId);

        Assert.Equal("Inactive", capturedResult.Rows[1].Values[0]);
        Assert.Equal(50.0, capturedResult.Rows[1].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_AppliesInMemorySkip()
    {
        var config = CreateConfig(skip: 1);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        var fieldAggResults = new[]
        {
            CreateFieldAggregationResult(["status"], ["A"], 1,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 10.0 }]),
            CreateFieldAggregationResult(["status"], ["B"], 2,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 20.0 }]),
            CreateFieldAggregationResult(["status"], ["C"], 3,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 30.0 }])
        };
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: fieldAggResults));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(2, capturedResult!.Rows.Count);
        Assert.Equal("B", capturedResult.Rows[0].Values[0]);
        Assert.Equal("C", capturedResult.Rows[1].Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_AppliesInMemoryTake()
    {
        var config = CreateConfig(take: 2);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        var fieldAggResults = new[]
        {
            CreateFieldAggregationResult(["status"], ["A"], 1,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 10.0 }]),
            CreateFieldAggregationResult(["status"], ["B"], 2,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 20.0 }]),
            CreateFieldAggregationResult(["status"], ["C"], 3,
                sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 30.0 }])
        };
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: fieldAggResults));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(2, capturedResult!.Rows.Count);
        Assert.Equal("A", capturedResult.Rows[0].Values[0]);
        Assert.Equal("B", capturedResult.Rows[1].Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_NullResult_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: null));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    #endregion

    #region Simple Stream-Data Query Tests

    private static readonly OctoObjectId TestArchiveRtId = new("000000000000000000000042");

    private void SetupSimpleStreamDataQuery(RtSimpleSdQuery simpleSdQuery)
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(simpleSdQuery));
    }

    private void SetupExecuteQueryResult(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(
                A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataQueryOptions o) => _capturedStreamOptions = o)
            .Returns(Task.FromResult(result));
    }

    private StreamDataQueryOptions? _capturedStreamOptions;

    private static RtSimpleSdQuery CreateSimpleStreamDataQuery(
        string[] columns, string? archiveRtId = "000000000000000000000042")
    {
        return new RtSimpleSdQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            ArchiveRtId = archiveRtId!,
            Columns = new AttributeStringValueList(columns.ToList())
        };
    }

    private static StreamDataQueryResult CreateStreamDataResult(params StreamDataRow[] rows)
    {
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Length };
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_BuildsTimeSeriesResult()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));

        var ts = new DateTime(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);
        var row = new StreamDataRow
        {
            RtId = new OctoObjectId("000000000000000000000123"),
            Timestamp = ts,
            Values = new Dictionary<string, object?> { ["temperature"] = 21.5 }
        };
        SetupExecuteQueryResult(CreateStreamDataResult(row));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        // Leading Timestamp column, then the projected attribute columns.
        Assert.Equal(2, capturedResult!.Columns.Count);
        Assert.Equal("Timestamp", capturedResult.Columns[0].Header);
        Assert.Equal("temperature", capturedResult.Columns[1].Header);

        Assert.Single(capturedResult.Rows);
        Assert.Equal(ts, capturedResult.Rows[0].Values[0]);
        Assert.Equal(21.5, capturedResult.Rows[0].Values[1]);
        Assert.Equal(new OctoObjectId("000000000000000000000123"), capturedResult.Rows[0].RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_MapsPhysicalColumnNames()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // Columns as the user projects them (dotted / mixed-case) plus a standard column.
        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(
            ["window_start", "amount.value", "obisCode", "was_updated"]));

        // The store keys Values by the physical column name: dots stripped + lower-cased. Standard
        // columns (window_start, was_updated) already equal their physical name.
        var row = new StreamDataRow
        {
            Timestamp = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?>
            {
                ["window_start"] = new DateTime(2026, 6, 9, 22, 0, 0, DateTimeKind.Utc),
                ["amountvalue"] = 42.5,
                ["obiscode"] = "1-0:1.8.0",
                ["was_updated"] = true
            }
        };
        SetupExecuteQueryResult(CreateStreamDataResult(row));

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        // Headers keep the user's attribute-path form (Timestamp prepended).
        Assert.Equal(["Timestamp", "window_start", "amount.value", "obisCode", "was_updated"],
            capturedResult!.Columns.Select(c => c.Header));

        var rowValues = capturedResult.Rows.Single().Values;
        Assert.Equal(new DateTime(2026, 6, 9, 22, 0, 0, DateTimeKind.Utc), rowValues[1]);
        Assert.Equal(42.5, rowValues[2]);           // amount.value -> amountvalue
        Assert.Equal("1-0:1.8.0", rowValues[3]);    // obisCode -> obiscode
        Assert.Equal(true, rowValues[4]);           // was_updated (standard column, exact match)
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_AppliesConfigOverrides()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = from,
            To = to,
            Limit = 500
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var options = _capturedStreamOptions;
        Assert.NotNull(options);
        Assert.Equal(from, options!.From);
        Assert.Equal(to, options.To);
        Assert.Equal(500, options.Limit);
        A.CallTo(() => _streamDataRepository.ExecuteQueryAsync(TestArchiveRtId, A<StreamDataQueryOptions>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_MissingArchiveRtId_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"], archiveRtId: null));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_StreamDataNotEnabled_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns((IStreamDataRepository?)null);

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    #endregion

    #region Aggregation / Grouped Stream-Data Query Tests

    private void SetupPersistentQuery(RtPersistentQuery query)
    {
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(query));
    }

    private void SetupExecuteAggregationResult(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataAggregationQueryOptions>._))
            .Returns(Task.FromResult(result));
    }

    private void SetupExecuteGroupedAggregationResult(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteGroupedAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataGroupedAggregationQueryOptions>._))
            .Returns(Task.FromResult(result));
    }

    private static RtAggregationSdQuery CreateAggregationStreamDataQuery(
        params (string path, RtAggregationTypesEnum type)[] columns)
    {
        var query = new RtAggregationSdQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            ArchiveRtId = "000000000000000000000042"
        };
        foreach (var (path, type) in columns)
        {
            query.Columns.Add(new RtAggregationQueryColumnRecord { AttributePath = path, AggregationType = type });
        }

        return query;
    }

    private static RtGroupingAggregationSdQuery CreateGroupedAggregationStreamDataQuery(
        string[] groupingColumns, params (string path, RtAggregationTypesEnum type)[] columns)
    {
        var query = new RtGroupingAggregationSdQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            ArchiveRtId = "000000000000000000000042",
            GroupingColumns = new AttributeStringValueList(groupingColumns.ToList())
        };
        foreach (var (path, type) in columns)
        {
            query.Columns.Add(new RtAggregationQueryColumnRecord { AttributePath = path, AggregationType = type });
        }

        return query;
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationStreamDataQuery_BuildsSingleRow()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateAggregationStreamDataQuery(
            ("Temperature", RtAggregationTypesEnum.Average),
            ("Amount.Value", RtAggregationTypesEnum.Sum)));

        // Store keys aggregates by the friendly output name {physicalColumn}_{funcToken}.
        var row = new StreamDataRow
        {
            Values = new Dictionary<string, object?>
            {
                ["temperature_avg"] = 21.5,
                ["amountvalue_sum"] = 302.0
            }
        };
        SetupExecuteAggregationResult(new StreamDataQueryResult { Rows = [row], TotalCount = 1 });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(["Temperature", "Amount.Value"], capturedResult!.Columns.Select(col => col.Header));
        Assert.Single(capturedResult.Rows);
        Assert.Equal(21.5, capturedResult.Rows[0].Values[0]);
        Assert.Equal(302.0, capturedResult.Rows[0].Values[1]);
        Assert.Null(capturedResult.Rows[0].RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationStreamDataQuery_BuildsGroupedRows()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateGroupedAggregationStreamDataQuery(
            ["SerialNumber"], ("Temperature", RtAggregationTypesEnum.Sum)));

        var rows = new[]
        {
            new StreamDataRow
            {
                Values = new Dictionary<string, object?>
                {
                    ["serialnumber"] = "A",
                    ["temperature_sum"] = 100.0
                }
            },
            new StreamDataRow
            {
                Values = new Dictionary<string, object?>
                {
                    ["serialnumber"] = "B",
                    ["temperature_sum"] = 50.0
                }
            }
        };
        SetupExecuteGroupedAggregationResult(new StreamDataQueryResult { Rows = rows, TotalCount = 2 });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(["SerialNumber", "Temperature"], capturedResult!.Columns.Select(col => col.Header));
        Assert.Equal(2, capturedResult.Rows.Count);
        Assert.Equal("A", capturedResult.Rows[0].Values[0]);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[1]);
        Assert.Equal("B", capturedResult.Rows[1].Values[0]);
        Assert.Equal(50.0, capturedResult.Rows[1].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(new RtDownsamplingSdQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            ArchiveRtId = "000000000000000000000042"
        });

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    #endregion

    #region Transaction Tests

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_StartsAndCommitsTransaction()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Count);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            countStats: [new StatisticsResult { AttributePath = "quantity", Value = 5L }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    #endregion

    #region Helper Methods

    private static RtAggregationRtQuery CreateTestAggregationQuery(
        string attributePath, RtAggregationTypesEnum aggregationType)
    {
        var query = new RtAggregationRtQuery { QueryCkTypeId = "TestModel/TestType" };
        var column = new RtAggregationQueryColumnRecord
        {
            AttributePath = attributePath,
            AggregationType = aggregationType
        };
        query.Columns.Add(column);
        return query;
    }

    private static RtGroupingAggregationRtQuery CreateTestGroupedAggregationQuery(
        string[] groupingColumns, string aggAttributePath, RtAggregationTypesEnum aggregationType)
    {
        var query = new RtGroupingAggregationRtQuery { QueryCkTypeId = "TestModel/TestType" };
        query.GroupingColumns = new AttributeStringValueList(groupingColumns.ToList());
        var column = new RtAggregationQueryColumnRecord
        {
            AttributePath = aggAttributePath,
            AggregationType = aggregationType
        };
        query.Columns.Add(column);
        return query;
    }

    private static AggregationResult CreateAggregationResult(
        IEnumerable<StatisticsResult>? countStats = null,
        IEnumerable<StatisticsResult>? minStats = null,
        IEnumerable<StatisticsResult>? maxStats = null,
        IEnumerable<StatisticsResult>? avgStats = null,
        IEnumerable<StatisticsResult>? sumStats = null)
    {
        return new AggregationResult(
            0,
            countStats ?? [],
            minStats ?? [],
            maxStats ?? [],
            avgStats ?? [],
            sumStats ?? []);
    }

    private static FieldAggregationResult CreateFieldAggregationResult(
        string[] groupByPaths, object?[] keys, long count,
        IEnumerable<StatisticsResult>? countStats = null,
        IEnumerable<StatisticsResult>? minStats = null,
        IEnumerable<StatisticsResult>? maxStats = null,
        IEnumerable<StatisticsResult>? avgStats = null,
        IEnumerable<StatisticsResult>? sumStats = null)
    {
        return new FieldAggregationResult(
            groupByPaths,
            keys,
            count,
            countStats ?? [],
            minStats ?? [],
            maxStats ?? [],
            avgStats ?? [],
            sumStats ?? []);
    }

    #endregion
}

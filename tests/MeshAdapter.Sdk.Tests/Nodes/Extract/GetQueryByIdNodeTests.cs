using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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

public class GetQueryByIdNodeTests : SessionNodeTestBase
{
    private static readonly OctoObjectId TestQueryRtId = new("000000000000000000000099");
    private static readonly CkId<CkTypeId> TestCkTypeId = new("TestModel", new CkTypeId("TestType-1"));

    /// <summary>
    /// Snapshot of the archive the stream-data queries read. Its columns are what the field resolver
    /// registers, so a persisted query column only maps to a storage key if it appears here.
    /// </summary>
    private static readonly ArchiveSnapshot TestArchiveSnapshot = new(
        TestArchiveRtId,
        new RtCkId<CkTypeId>("TestModel/TestType-1"),
        CkArchiveStatus.Activated,
        "test-archive",
        [
            new CkArchiveColumnSpec("Temperature", false, false),
            new CkArchiveColumnSpec("Amount.Value", false, false),
            new CkArchiveColumnSpec("Amount.Unit", false, false),
            new CkArchiveColumnSpec("SerialNumber", false, false),
            new CkArchiveColumnSpec("obisCode", false, false)
        ])
    {
        // Windowed, so the resolver also knows window_start / was_updated — the physical-column test
        // projects them alongside the attribute paths.
        IsTimeRange = true
    };
    private const string TestTenantId = "test-tenant";

    private readonly ICkCacheService _ckCacheService;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;
    private readonly IArchiveRuntimeStore _archiveStore;
    private readonly IRollupArchiveRuntimeStore _rollupStore;

    public GetQueryByIdNodeTests()
    {
        _ckCacheService = A.Fake<ICkCacheService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();
        _archiveStore = A.Fake<IArchiveRuntimeStore>();
        _rollupStore = A.Fake<IRollupArchiveRuntimeStore>();

        A.CallTo(() => EtlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => TenantRepository.TenantId).Returns(TestTenantId);

        A.CallTo(() => _systemContext.FindTenantContextAsync(TestTenantId))
            .Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
        A.CallTo(() => _tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
        A.CallTo(() => _tenantContext.GetRollupArchiveRuntimeStore()).Returns(_rollupStore);
        // Resolution-aware tests drive the real SeriesResolutionService; the default is an empty
        // ladder (no base archive, no rollups) so a test only sets up what it exercises.
        A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._))
            .Returns(Task.FromResult<ArchiveSnapshot?>(null));
        // The stream-data path needs the queried archive's snapshot to build the field resolver that
        // maps each persisted column onto its storage key (AB#4764). Registered for the query archive
        // only, so the ladder default above still holds for every other id.
        A.CallTo(() => _archiveStore.GetAsync(TestArchiveRtId))
            .Returns(Task.FromResult<ArchiveSnapshot?>(TestArchiveSnapshot));
        A.CallTo(() => _rollupStore.EnumerateAsync())
            .Returns(AsAsyncEnumerable(Array.Empty<RollupArchiveSnapshot>()));

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
        return new GetQueryByIdNode(next, EtlContext, _ckCacheService, _systemContext);
    }

    private void SetupQueryEntityNotFound()
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(null));
    }

    private void SetupSimpleQuery(RtSimpleRtQuery simpleQuery)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(simpleQuery));
    }

    private void SetupAggregationQuery(RtAggregationRtQuery aggregationQuery)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(aggregationQuery));
    }

    private void SetupGroupingAggregationQuery(RtGroupingAggregationRtQuery groupedQuery)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(groupedQuery));
    }

    private void SetupGraphByTypeResult(IResultSet<RtEntityGraphItem> resultSet)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesGraphByTypeAsync(
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

        A.CallTo(() => TenantRepository.GetRtEntitiesGraphByTypeAsync(
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

        A.CallTo(() => TenantRepository.GetRtEntitiesGraphByTypeAsync(
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

        A.CallTo(() => TenantRepository.GetRtEntitiesGraphByTypeAsync(
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
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
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
    public async Task ProcessObjectAsync_SnapshotUnavailable_WarnsAboutTheReducedResolution()
    {
        // Without a snapshot only the standard columns resolve, which brings back the AB#4764 symptom
        // for anything computed. Degrading is deliberate here (the downsampling path relies on it), so
        // the safeguard is that it cannot happen quietly — and this store returns null without throwing,
        // the path that used to produce no log line at all.
        A.CallTo(() => _archiveStore.GetAsync(TestArchiveRtId))
            .Returns(Task.FromResult<ArchiveSnapshot?>(null));

        var config = CreateConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Warned, and the query still ran — the fallback is a degradation, not a failure.
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("computed column may come back empty"), A<object[]>._))
            .MustHaveHappened();
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
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
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

    #endregion

    #region Downsampling Stream-Data Query Tests

    private static readonly DateTime DownsamplingFrom = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private StreamDataDownsamplingQueryOptions? _capturedDownsamplingOptions;
    private OctoObjectId? _capturedDownsamplingArchiveRtId;

    private void SetupExecuteDownsamplingResult(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteDownsamplingQueryAsync(
                A<OctoObjectId>._, A<StreamDataDownsamplingQueryOptions>._))
            .Invokes((OctoObjectId archiveRtId, StreamDataDownsamplingQueryOptions o) =>
            {
                _capturedDownsamplingArchiveRtId = archiveRtId;
                _capturedDownsamplingOptions = o;
            })
            .Returns(Task.FromResult(result));
    }

    private static RtDownsamplingSdQuery CreateDownsamplingStreamDataQuery(
        DateTime? from = null, DateTime? to = null, int? limit = null,
        params (string path, RtAggregationTypesEnum type)[] columns)
    {
        var query = new RtDownsamplingSdQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            ArchiveRtId = "000000000000000000000042",
            From = from,
            To = to,
            Limit = limit
        };
        foreach (var (path, type) in columns)
        {
            query.Columns.Add(new RtAggregationQueryColumnRecord { AttributePath = path, AggregationType = type });
        }

        return query;
    }

    /// <summary>
    /// A downsampling bin as the storage layer returns it: the bin start plus one aggregate keyed by
    /// the friendly output name <c>{physicalColumn}_{funcToken}</c>. Empty bins carry null aggregates.
    /// </summary>
    private static StreamDataRow CreateBinRow(DateTime timestamp, OctoObjectId? rtId = null,
        params (string key, object? value)[] values)
    {
        return new StreamDataRow
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>("TestModel/TestType"),
            Timestamp = timestamp,
            Values = values.ToDictionary(v => v.key, v => v.value)
        };
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_BuildsBinnedTimeSeriesResult()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddHours(2), 2,
            ("Temperature", RtAggregationTypesEnum.Average),
            ("Amount.Value", RtAggregationTypesEnum.Sum)));

        SetupExecuteDownsamplingResult(new StreamDataQueryResult
        {
            Rows =
            [
                CreateBinRow(DownsamplingFrom, values: [("temperature_avg", 21.5), ("amountvalue_sum", 100.0)]),
                CreateBinRow(DownsamplingFrom.AddHours(1),
                    values: [("temperature_avg", 22.5), ("amountvalue_sum", 110.0)])
            ],
            TotalCount = 2
        });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        // Leading Timestamp (the bin start), then one column per aggregation headed by its path.
        Assert.Equal(["Timestamp", "Temperature", "Amount.Value"],
            capturedResult!.Columns.Select(col => col.Header));
        Assert.Equal(2, capturedResult.Rows.Count);
        Assert.Equal(DownsamplingFrom, capturedResult.Rows[0].Values[0]);
        Assert.Equal(21.5, capturedResult.Rows[0].Values[1]);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[2]);
        Assert.Equal(DownsamplingFrom.AddHours(1), capturedResult.Rows[1].Values[0]);
        Assert.NotNull(capturedResult.Rows[0].CkTypeId);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_EmptyBinYieldsNullAggregates()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddHours(1), 1,
            ("Temperature", RtAggregationTypesEnum.Average)));

        SetupExecuteDownsamplingResult(new StreamDataQueryResult
        {
            Rows = [CreateBinRow(DownsamplingFrom, values: [("temperature_avg", null)])],
            TotalCount = 1
        });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The empty bin keeps its timestamp; the aggregate is null rather than the row being dropped.
        Assert.Single(capturedResult!.Rows);
        Assert.Equal(DownsamplingFrom, capturedResult.Rows[0].Values[0]);
        Assert.Null(capturedResult.Rows[0].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_PassesPersistedRangeAndLimit()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        var options = _capturedDownsamplingOptions;
        Assert.NotNull(options);
        Assert.Equal(DownsamplingFrom, options!.From);
        Assert.Equal(DownsamplingFrom.AddDays(1), options.To);
        Assert.Equal(24, options.Limit);
        Assert.Null(options.GroupByColumnPaths);
        Assert.Equal(AggregationFunction.Average, Assert.Single(options.AggregationColumns).Function);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_AppliesConfigOverrides()
    {
        var overrideFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = overrideFrom,
            To = overrideFrom.AddHours(6),
            Limit = 6
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(overrideFrom, _capturedDownsamplingOptions!.From);
        Assert.Equal(overrideFrom.AddHours(6), _capturedDownsamplingOptions.To);
        Assert.Equal(6, _capturedDownsamplingOptions.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_ResolvesRangeFromPipelineData()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.range.from",
            ToPath = "$.range.to",
            LimitPath = "$.range.points"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPathValue(dataContext, "$.range.from", DownsamplingFrom);
        SetupPathValue(dataContext, "$.range.to", DownsamplingFrom.AddHours(12));
        SetupPathValue(dataContext, "$.range.points", 12);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            columns: ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(DownsamplingFrom, _capturedDownsamplingOptions!.From);
        Assert.Equal(DownsamplingFrom.AddHours(12), _capturedDownsamplingOptions.To);
        Assert.Equal(12, _capturedDownsamplingOptions.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_UnspecifiedKindRangeReadsAsUtc()
    {
        // A literal deserialized from pipeline JSON without an offset arrives as Unspecified. The
        // node's contract is UTC, so the window handed to the storage layer must be a UTC instant.
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified),
            To = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Unspecified),
            Limit = 24
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            columns: ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), _capturedDownsamplingOptions!.From);
        Assert.Equal(DateTimeKind.Utc, _capturedDownsamplingOptions.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedDownsamplingOptions.To!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_WithoutTimeRange_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            to: DownsamplingFrom.AddDays(1), limit: 24,
            columns: ("Temperature", RtAggregationTypesEnum.Average)));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_FromNotBeforeTo_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom, 24,
            ("Temperature", RtAggregationTypesEnum.Average)));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_NonPositiveLimit_Throws(int? limit)
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), limit,
            ("Temperature", RtAggregationTypesEnum.Average)));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_WithoutColumns_Throws()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_AppliesInMemorySkipAndTake()
    {
        var config = CreateConfig(skip: 1, take: 2);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddHours(4), 4,
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult
        {
            Rows = Enumerable.Range(0, 4)
                .Select(i => CreateBinRow(DownsamplingFrom.AddHours(i),
                    values: [("temperature_avg", (double)i)]))
                .ToArray(),
            TotalCount = 4
        });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, capturedResult!.Rows.Count);
        Assert.Equal(DownsamplingFrom.AddHours(1), capturedResult.Rows[0].Values[0]);
        Assert.Equal(DownsamplingFrom.AddHours(2), capturedResult.Rows[1].Values[0]);
        // Pagination is never pushed down: the storage layer's downsampling path ignores it.
        Assert.Null(_capturedDownsamplingOptions!.Offset);
        Assert.Null(_capturedDownsamplingOptions.PageSize);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDownsamplingStreamDataQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddHours(1), 1,
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    #endregion

    #region Resolution-Aware Archive Selection Tests

    private static readonly OctoObjectId TestRollupRtId = new("000000000000000000000777");

    /// <summary>
    /// The rollup store's enumeration is an <see cref="IAsyncEnumerable{T}" />; the test project does
    /// not reference System.Linq.Async, so it is materialised here.
    /// </summary>
    private static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Base rung of the resolution ladder. <paramref name="period" /> is the declared native grain —
    /// null for a raw archive, whose sampling interval is undeclared.
    /// </summary>
    private void SetupBaseArchive(TimeSpan? period)
    {
        A.CallTo(() => _archiveStore.GetAsync(TestArchiveRtId))
            .Returns(Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(
                TestArchiveRtId,
                new RtCkId<CkTypeId>("TestModel/TestType"),
                CkArchiveStatus.Activated,
                "base",
                [])
            {
                Period = period
            }));
    }

    private void SetupRollups(params RollupArchiveSnapshot[] rollups)
    {
        A.CallTo(() => _rollupStore.EnumerateAsync()).Returns(AsAsyncEnumerable(rollups));
        SetupRollupLookup(rollups);
    }

    /// <summary>
    /// A rollup rung. The watermark defaults to a date past every window used here, so a test only has
    /// to state it when it exercises the "rollup has not caught up" branch.
    /// </summary>
    private static RollupArchiveSnapshot CreateRollup(OctoObjectId rtId, TimeSpan bucketSize,
        string sourcePath, CkRollupFunction function, OctoObjectId? sourceArchiveRtId = null,
        DateTime? lastAggregatedBucketEnd = null, BucketAlignment alignment = BucketAlignment.FixedSize)
    {
        return new RollupArchiveSnapshot(
            rtId,
            new RtCkId<CkTypeId>("TestModel/TestType"),
            CkArchiveStatus.Activated,
            "rollup",
            sourceArchiveRtId ?? TestArchiveRtId,
            bucketSize,
            TimeSpan.FromMinutes(5),
            lastAggregatedBucketEnd ?? new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            [new CkRollupAggregationSpec(sourcePath, function, null)],
            FrozenUntil: null)
        {
            BucketAlignment = alignment
        };
    }

    /// <summary>
    /// The rollup store must also answer <c>GetAsync</c> for the rung the resolver picks — the node reads
    /// the chosen rollup's bucket size and watermark to decide whether it can answer the query exactly.
    /// </summary>
    private void SetupRollupLookup(params RollupArchiveSnapshot[] rollups)
    {
        foreach (var rollup in rollups)
        {
            A.CallTo(() => _rollupStore.GetAsync(rollup.RtId))
                .Returns(Task.FromResult<RollupArchiveSnapshot?>(rollup));
        }
    }

    /// <summary>
    /// Archive selection is inherent to a downsampling query, so the plain configuration already
    /// exercises it. The optional aggregation override mirrors what a query definition can carry.
    /// </summary>
    private static GetQueryByIdNodeConfiguration CreateDownsamplingConfig(
        AggregationTypesDto? aggregation = null)
    {
        return new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            Aggregation = aggregation
        };
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithoutRollupStore_WarnsAndUsesPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        A.CallTo(() => _tenantContext.GetRollupArchiveRuntimeStore()).Returns(null);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(DownsamplingFrom, _capturedDownsamplingOptions!.From);
        Assert.Equal(DownsamplingFrom.AddDays(1), _capturedDownsamplingOptions.To);
        Assert.Equal(24, _capturedDownsamplingOptions.Limit);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("No rollup-archive store"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithUnalignedRangeStart_ReadsPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value", CkRollupFunction.Sum));

        // 24 h / 24 points ⇒ 1 h bins, a whole multiple of the hourly rollup — but the range starts at
        // :07:13, so every stored hour would straddle a bin boundary. The rollup is declined.
        var unalignedFrom = DownsamplingFrom.AddMinutes(7).AddSeconds(13);
        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            unalignedFrom, unalignedFrom.AddHours(24), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(unalignedFrom, _capturedDownsamplingOptions!.From);
        Assert.Equal(unalignedFrom.AddHours(24), _capturedDownsamplingOptions.To);
        Assert.Equal(24, _capturedDownsamplingOptions.Limit);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("bucket grid"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWhenBaseFits_QueriesBaseArchiveWithRequestedBuckets()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        // 24 h of hourly data = 24 native points, well within the requested 100 → no reduction needed.
        SetupBaseArchive(TimeSpan.FromHours(1));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 100,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        // The resolver's own point count (24 native points) does not override the requested buckets.
        Assert.Equal(100, _capturedDownsamplingOptions!.Limit);
        Assert.Equal(DownsamplingFrom, _capturedDownsamplingOptions.From);
        Assert.Equal(DownsamplingFrom.AddDays(1), _capturedDownsamplingOptions.To);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithoutSuitableRollup_WarnsAndUsesBaseArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        // 24 h of 15-min data = 96 native points; only 4 were asked for and no rollup can reduce a
        // Sum series, so the resolver refuses to reduce and reports the truthful point count.
        SetupBaseArchive(TimeSpan.FromMinutes(15));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 4,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        // The refusal is a warning only; the query still asks for exactly the buckets it defines.
        Assert.Equal(4, _capturedDownsamplingOptions!.Limit);
        Assert.Equal(DownsamplingFrom, _capturedDownsamplingOptions.From);
        Assert.Equal(DownsamplingFrom.AddDays(1), _capturedDownsamplingOptions.To);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("NoSuitableRollup"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithOnlyCoarseRollup_WarnsAndFallsBackToBaseArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        // 7 d window, 600 points requested (ideal ≈ 16.8 min) but only a daily rollup exists, and the
        // 15-min base (672 points) does not fit either → ResolutionLimited. The daily rollup cannot answer
        // 1008-second bins exactly, so the base archive is read after all.
        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromDays(1), "Amount.Value", CkRollupFunction.Sum));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(7), 600,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(600, _capturedDownsamplingOptions!.Limit);
        Assert.Equal(DownsamplingFrom, _capturedDownsamplingOptions.From);
        Assert.Equal(DownsamplingFrom.AddDays(7), _capturedDownsamplingOptions.To);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("ResolutionLimited"), A<object[]>._))
            .MustHaveHappened();
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("whole multiple"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithUnknownBaseGrain_WarnsAndUsesBaseArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        // Raw archive: no declared Period, so the resolver cannot tell whether reduction is needed.
        SetupBaseArchive(null);

        var unalignedFrom = DownsamplingFrom.AddMinutes(7);
        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            unalignedFrom, unalignedFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(24, _capturedDownsamplingOptions!.Limit);
        Assert.Equal(unalignedFrom, _capturedDownsamplingOptions.From);
        Assert.Equal(unalignedFrom.AddDays(1), _capturedDownsamplingOptions.To);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("UnknownBaseGrain"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithEmptyLadder_WarnsAndKeepsPersistedArchive()
    {
        // An empty ladder means the archive itself does not resolve, so the shared default that
        // supplies its snapshot has to be taken back for this test.
        A.CallTo(() => _archiveStore.GetAsync(TestArchiveRtId))
            .Returns(Task.FromResult<ArchiveSnapshot?>(null));

        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        // The base archive entity cannot be read → no ladder at all.
        var unalignedFrom = DownsamplingFrom.AddMinutes(7);
        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            unalignedFrom, unalignedFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(24, _capturedDownsamplingOptions!.Limit);
        Assert.Equal(unalignedFrom, _capturedDownsamplingOptions.From);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("EmptyLadder"), A<object[]>._))
            .MustHaveHappened();
    }

    /// <summary>
    /// Field report on AB#4725, part one: a 7-day window with 10 buckets gives 16 h 48 min bins, which is
    /// not a whole multiple of the hourly rollup. One hourly window per bin would straddle a bin boundary
    /// and drop out (measured: 8 of 168 hours, −4.1 % on the total, −27 % on a single bin), so the rollup
    /// must be declined and the archive the query names read instead.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithBinWidthNotMultipleOfGrain_ReadsPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);

        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value", CkRollupFunction.Sum));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            from, to, 10, ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(from, _capturedDownsamplingOptions!.From);
        Assert.Equal(to, _capturedDownsamplingOptions.To);
        Assert.Equal(10, _capturedDownsamplingOptions.Limit);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("whole multiple"), A<object[]>._))
            .MustHaveHappened();
    }

    /// <summary>
    /// Field report on AB#4725, part two: the same window with 12 buckets gives 14 h bins — a whole
    /// multiple of the hourly rollup, with the range start on the hour grid. Here the rollup is read (that
    /// is the performance win) and the values match the base archive to the last digit.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithBinWidthMultipleOfGrain_ReadsRollup()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);

        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value", CkRollupFunction.Sum));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            from, to, 12, ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestRollupRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(from, _capturedDownsamplingOptions!.From);
        Assert.Equal(to, _capturedDownsamplingOptions.To);
        Assert.Equal(12, _capturedDownsamplingOptions.Limit);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustNotHaveHappened();
        // The chosen archive is reported so a pipeline author can see where the numbers came from.
        A.CallTo(() => logger.Info(A<string>._, A<string>._,
                A<string>.That.Contains(TestRollupRtId.ToString()), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithCalendarAlignedRollup_ReadsPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(28);

        SetupBaseArchive(TimeSpan.FromHours(1));
        // 28 d / 28 buckets = 1 d bins, arithmetically a multiple of the rung's width — but civil days
        // shift with DST, so a fixed-width bin cannot be trusted to contain them.
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromDays(1), "Amount.Value",
            CkRollupFunction.Sum, alignment: BucketAlignment.CalendarDay));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            from, to, 28, ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("CalendarDay-aligned"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWithRollupBehindRangeEnd_ReadsPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);

        SetupBaseArchive(TimeSpan.FromMinutes(15));
        // Bin geometry is fine, but the rollup has only aggregated up to halfway through the window —
        // the newest bins would read low.
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value",
            CkRollupFunction.Sum, lastAggregatedBucketEnd: from.AddDays(3)));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            from, to, 12, ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("only aggregated up to"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAware_AggregationOverrideAppliesToRungAndResultColumn()
    {
        var config = CreateDownsamplingConfig(AggregationTypesDto.Sum);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value", CkRollupFunction.Sum));

        // The query persists Average, but the rollup only stores Sum — the override matches the rung
        // AND makes the query read back the column the rollup materialises.
        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Average)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult
        {
            Rows = [CreateBinRow(DownsamplingFrom, values: [("amountvalue_sum", 500.0)])],
            TotalCount = 1
        });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestRollupRtId, _capturedDownsamplingArchiveRtId);
        Assert.Equal(AggregationFunction.Sum,
            Assert.Single(_capturedDownsamplingOptions!.AggregationColumns).Function);
        // The header keeps the persisted attribute path; only the value lookup follows the override.
        Assert.Equal(["Timestamp", "Amount.Value"], capturedResult!.Columns.Select(col => col.Header));
        Assert.Equal(500.0, capturedResult.Rows[0].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationOverride_AppliesToEveryColumn()
    {
        var config = CreateDownsamplingConfig(AggregationTypesDto.Sum);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // Two columns with different persisted aggregations — the single override replaces both.
        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Temperature", RtAggregationTypesEnum.Average),
            ("Amount.Value", RtAggregationTypesEnum.Minimum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult
        {
            Rows =
            [
                CreateBinRow(DownsamplingFrom,
                    values: [("temperature_sum", 42.0), ("amountvalue_sum", 500.0)])
            ],
            TotalCount = 1
        });

        QueryResult? capturedResult = null;
        CaptureSetCall(dataContext, "$.queryResult", qr => capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.All(_capturedDownsamplingOptions!.AggregationColumns,
            col => Assert.Equal(AggregationFunction.Sum, col.Function));
        // Headers keep the persisted attribute paths; only the aggregation follows the override.
        Assert.Equal(["Timestamp", "Temperature", "Amount.Value"],
            capturedResult!.Columns.Select(col => col.Header));
        Assert.Equal(42.0, capturedResult.Rows[0].Values[1]);
        Assert.Equal(500.0, capturedResult.Rows[0].Values[2]);
    }

    [Theory]
    [InlineData(AggregationTypesDto.None)]
    [InlineData(AggregationTypesDto.TimeWeightedAverage)]
    [InlineData(AggregationTypesDto.StateDuration)]
    public async Task ProcessObjectAsync_WithUnsupportedAggregationOverride_Throws(AggregationTypesDto aggregation)
    {
        var config = CreateDownsamplingConfig(aggregation);
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWhenRollupLookupThrows_ReadsPersistedArchive()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);
        SetupBaseArchive(TimeSpan.FromMinutes(15));
        SetupRollups(CreateRollup(TestRollupRtId, TimeSpan.FromHours(1), "Amount.Value", CkRollupFunction.Sum));
        // The ladder resolves, but reading the chosen rollup's definition fails. Reading the archive the
        // query names is still the correct answer, so the node degrades instead of failing the pipeline.
        A.CallTo(() => _rollupStore.GetAsync(TestRollupRtId))
            .Throws(new InvalidOperationException("rollup store unavailable"));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            from, from.AddDays(7), 12, ("Amount.Value", RtAggregationTypesEnum.Sum)));
        SetupExecuteDownsamplingResult(new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(TestArchiveRtId, _capturedDownsamplingArchiveRtId);
        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("rollup store unavailable"), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolutionAwareWhenArchiveStoreThrows_WrapsAsPipelineException()
    {
        var config = CreateDownsamplingConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._))
            .Throws(new InvalidOperationException("archive store unavailable"));

        SetupPersistentQuery(CreateDownsamplingStreamDataQuery(
            DownsamplingFrom, DownsamplingFrom.AddDays(1), 24,
            ("Amount.Value", RtAggregationTypesEnum.Sum)));

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    #endregion

    #region Stream-Data Time Range From Pipeline Data (FromPath / ToPath / LimitPath)

    /// <summary>
    /// Fakes a scalar read at <paramref name="path" />. Production code calls
    /// <c>GetValue(path)</c>, i.e. with <c>parseDateStrings: true</c>, which is why ISO-8601 strings
    /// arrive at the node already boxed as <see cref="DateTime" /> (see JsonScalar.ToClr).
    /// </summary>
    private static void SetupPathValue(IDataContext dataContext, string path, object? value)
    {
        A.CallTo(() => dataContext.GetValue(path, true)).Returns(value);
    }

    private StreamDataAggregationQueryOptions? _capturedAggregationOptions;

    private void SetupExecuteAggregationResultCapturingOptions(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataAggregationQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataAggregationQueryOptions o) =>
                _capturedAggregationOptions = o)
            .Returns(Task.FromResult(result));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithFromPathAndToPath_ResolvesTimeRangeFromPipelineData()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.timeRange.from",
            ToPath = "$.timeRange.to"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // from: already a DateTime (the usual case — GetValue parses ISO-8601 strings).
        // to: a string ISO detection rejects, covered by the node's lenient parse arm.
        SetupPathValue(dataContext, "$.timeRange.from",
            new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc));
        SetupPathValue(dataContext, "$.timeRange.to", "2026-03-02 06:00:00");

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var options = _capturedStreamOptions;
        Assert.NotNull(options);
        Assert.Equal(new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc), options!.From);
        Assert.Equal(new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), options.To);
        Assert.Equal(DateTimeKind.Utc, options.To!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUnspecifiedKindAtFromPath_ReadsValueAsUtc()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.from"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // JSON without an offset ("2026-03-01T06:00:00") surfaces as Unspecified — the node's
        // contract is UTC, so it must not be shifted by the server's local time zone.
        SetupPathValue(dataContext, "$.from",
            new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Unspecified));

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc), _capturedStreamOptions!.From);
        Assert.Equal(DateTimeKind.Utc, _capturedStreamOptions.From!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithLiteralAndPath_LiteralWins()
    {
        var literalFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = literalFrom,
            FromPath = "$.from",
            Limit = 100,
            LimitPath = "$.limit"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPathValue(dataContext, "$.from", new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        SetupPathValue(dataContext, "$.limit", 7);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(literalFrom, _capturedStreamOptions!.From);
        Assert.Equal(100, _capturedStreamOptions.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithLimitPath_ResolvesRowCapFromPipelineData()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            LimitPath = "$.limit"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPathValue(dataContext, "$.limit", 250);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(250, _capturedStreamOptions!.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithStringLimitPathValue_ResolvesRowCapFromPipelineData()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            LimitPath = "$.limit"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // HTTP triggers deliver query-string values as strings.
        SetupPathValue(dataContext, "$.limit", "250");

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(250, _capturedStreamOptions!.Limit);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUnresolvableFromPath_UsesPersistedValue()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.missing"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        // An absent path yields null from GetValue in production (FakeItEasy would otherwise return
        // a dummy object for the object-typed return, so the null has to be configured explicitly).
        SetupPathValue(dataContext, "$.missing", null);

        var persistedFrom = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var query = CreateSimpleStreamDataQuery(["temperature"]);
        query.From = persistedFrom;
        SetupSimpleStreamDataQuery(query);
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(persistedFrom, _capturedStreamOptions!.From);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNonDateValueAtFromPath_Throws()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.from"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPathValue(dataContext, "$.from", "not-a-timestamp");

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNonIntegerValueAtLimitPath_Throws()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            LimitPath = "$.limit"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPathValue(dataContext, "$.limit", "many");

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationStreamDataQuery_ResolvesTimeRangeFromPaths()
    {
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            FromPath = "$.timeRange.from",
            ToPath = "$.timeRange.to"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        var from = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);
        SetupPathValue(dataContext, "$.timeRange.from", from);
        SetupPathValue(dataContext, "$.timeRange.to", to);

        SetupPersistentQuery(CreateAggregationStreamDataQuery(
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteAggregationResultCapturingOptions(
            new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(_capturedAggregationOptions);
        Assert.Equal(from, _capturedAggregationOptions!.From);
        Assert.Equal(to, _capturedAggregationOptions.To);
    }

    #endregion

    #region Stream-Data Literal Time Range Normalisation (UTC)

    // A literal From/To written into the node configuration without an offset
    // ("2026-06-01T00:00:00") is deserialized by STJ as DateTimeKind.Unspecified. The storage layer
    // normalises with ToUniversalTime() before rendering the timestamp literal, which reads
    // Unspecified as *local* time — so without the node normalising first, the queried window is
    // shifted by the host offset. The node's contract is UTC on every stream-data query kind.
    // Note: Assert.Equal(DateTime, DateTime) ignores Kind, hence the explicit Kind assertions.

    private static readonly DateTime UnspecifiedFrom = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime UnspecifiedTo = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime ExpectedUtcFrom = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpectedUtcTo = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static GetQueryByIdNodeConfiguration CreateUnspecifiedRangeConfig()
    {
        return new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = UnspecifiedFrom,
            To = UnspecifiedTo
        };
    }

    private StreamDataGroupedAggregationQueryOptions? _capturedGroupedAggregationOptions;

    private void SetupExecuteGroupedAggregationResultCapturingOptions(StreamDataQueryResult result)
    {
        A.CallTo(() => _streamDataRepository.ExecuteGroupedAggregationQueryAsync(
                A<OctoObjectId>._, A<StreamDataGroupedAggregationQueryOptions>._))
            .Invokes((OctoObjectId _, StreamDataGroupedAggregationQueryOptions o) =>
                _capturedGroupedAggregationOptions = o)
            .Returns(Task.FromResult(result));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_UnspecifiedKindLiteralReadsAsUtc()
    {
        var config = CreateUnspecifiedRangeConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(ExpectedUtcFrom, _capturedStreamOptions!.From);
        Assert.Equal(ExpectedUtcTo, _capturedStreamOptions.To);
        Assert.Equal(DateTimeKind.Utc, _capturedStreamOptions.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedStreamOptions.To!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationStreamDataQuery_UnspecifiedKindLiteralReadsAsUtc()
    {
        var config = CreateUnspecifiedRangeConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateAggregationStreamDataQuery(
            ("Temperature", RtAggregationTypesEnum.Average)));
        SetupExecuteAggregationResultCapturingOptions(
            new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(ExpectedUtcFrom, _capturedAggregationOptions!.From);
        Assert.Equal(ExpectedUtcTo, _capturedAggregationOptions.To);
        Assert.Equal(DateTimeKind.Utc, _capturedAggregationOptions.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedAggregationOptions.To!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationStreamDataQuery_UnspecifiedKindLiteralReadsAsUtc()
    {
        var config = CreateUnspecifiedRangeConfig();
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupPersistentQuery(CreateGroupedAggregationStreamDataQuery(
            ["SerialNumber"], ("Temperature", RtAggregationTypesEnum.Sum)));
        SetupExecuteGroupedAggregationResultCapturingOptions(
            new StreamDataQueryResult { Rows = [], TotalCount = 0 });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(ExpectedUtcFrom, _capturedGroupedAggregationOptions!.From);
        Assert.Equal(ExpectedUtcTo, _capturedGroupedAggregationOptions.To);
        Assert.Equal(DateTimeKind.Utc, _capturedGroupedAggregationOptions.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedGroupedAggregationOptions.To!.Value.Kind);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithLocalKindLiteral_ConvertsToUtcInsteadOfStamping()
    {
        // A Local literal carries a real instant, so it must be *converted* — not relabelled. Compared
        // against ToUniversalTime() rather than a fixed instant so the test holds in any host zone.
        var localFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Local);
        var localTo = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Local);
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = TestQueryRtId,
            TargetPath = "$.queryResult",
            From = localFrom,
            To = localTo
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetQueryByIdNodeConfiguration>(config);

        SetupSimpleStreamDataQuery(CreateSimpleStreamDataQuery(["temperature"]));
        SetupExecuteQueryResult(CreateStreamDataResult());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(localFrom.ToUniversalTime(), _capturedStreamOptions!.From);
        Assert.Equal(localTo.ToUniversalTime(), _capturedStreamOptions.To);
        Assert.Equal(DateTimeKind.Utc, _capturedStreamOptions.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, _capturedStreamOptions.To!.Value.Kind);
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

        A.CallTo(() => Session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
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

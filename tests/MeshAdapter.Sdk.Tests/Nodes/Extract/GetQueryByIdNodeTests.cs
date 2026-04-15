using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetQueryByIdNodeTests
{
    private static readonly OctoObjectId TestQueryRtId = new("000000000000000000000099");
    private static readonly CkId<CkTypeId> TestCkTypeId = new("TestModel", new CkTypeId("TestType-1"));
    private const string TestTenantId = "test-tenant";

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;
    private readonly ICkCacheService _ckCacheService;

    public GetQueryByIdNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();
        _ckCacheService = A.Fake<ICkCacheService>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _tenantRepository.TenantId).Returns(TestTenantId);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));

        // Set up CK cache to return a valid type graph for path evaluation
        var ckTypeDto = new CkCompiledTypeDto { TypeId = new CkTypeId("TestType-1") };
        var ckTypeGraph = new CkTypeGraph(TestCkTypeId, ckTypeDto);
        A.CallTo(() => _ckCacheService.GetRtCkType(TestTenantId, A<RtCkId<CkTypeId>>._))
            .Returns(ckTypeGraph);
        A.CallTo(() => _ckCacheService.TryGetCkType(TestTenantId, A<CkId<CkTypeId>>._, out ckTypeGraph))
            .Returns(true)
            .AssignsOutAndRefParameters(ckTypeGraph);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        GetQueryByIdNodeConfiguration config)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            "GetQueryById",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private GetQueryByIdNode CreateNode(NodeDelegate next)
    {
        return new GetQueryByIdNode(next, _etlContext, _ckCacheService);
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

    #region Query Not Found

    [Fact]
    public async Task ProcessObjectAsync_WithNonExistentQuery_DoesNotCallNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupQueryEntityNotFound();

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    #endregion

    #region Simple Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var simpleQuery = new RtSimpleRtQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            Columns = new AttributeStringValueList(["col1", "col2"])
        };
        SetupSimpleQuery(simpleQuery);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleQuery_PassesSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var simpleQuery = new RtSimpleRtQuery
        {
            QueryCkTypeId = "TestModel/TestType",
            Columns = new AttributeStringValueList(["col1"])
        };
        SetupSimpleQuery(simpleQuery);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .MustHaveHappenedOnceExactly();
    }

    #endregion

    #region Aggregation Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 42.5 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_PassesNullSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 0 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Aggregation queries should pass null for skip/take
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        var aggregationResult = CreateAggregationResult(
            sumStats: [new StatisticsResult { AttributePath = "quantity", Value = 42.5 }]);
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: aggregationResult));

        QueryResult? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, QueryResult qr) =>
                capturedResult = qr);

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
    public async Task ProcessObjectAsync_WithAggregationQuery_NullResult_DoesNotCallNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var aggregationQuery = CreateTestAggregationQuery("quantity", RtAggregationTypesEnum.Sum);
        SetupAggregationQuery(aggregationQuery);

        // No aggregation result
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(aggregationResult: null));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_MultipleColumns_ReturnsAllValues()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, QueryResult qr) =>
                capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        Assert.Equal(3, capturedResult!.Columns.Count);
        Assert.Single(capturedResult.Rows);
        Assert.Equal(3, capturedResult.Rows[0].Values.Count);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[0]); // Sum(quantity)
        Assert.Equal(25.5, capturedResult.Rows[0].Values[1]);  // Avg(price)
        Assert.Equal(4L, capturedResult.Rows[0].Values[2]);     // Count(quantity)
    }

    #endregion

    #region Grouped Aggregation Query Tests

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_CallsNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

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

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_PassesNullSkipTakeToRepository()
    {
        var config = CreateConfig(skip: 5, take: 10);
        var (dataContext, nodeContext, next) = PrepareTest(config);

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

        // Grouped aggregation queries should pass null for skip/take to the database
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, QueryResult qr) =>
                capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);

        // Columns: groupBy first, then aggregation
        Assert.Equal(2, capturedResult!.Columns.Count);
        Assert.Equal("status", capturedResult.Columns[0].Header);
        Assert.Equal("quantity", capturedResult.Columns[1].Header);

        // Rows: one per group
        Assert.Equal(2, capturedResult.Rows.Count);

        // First group
        Assert.Equal("Active", capturedResult.Rows[0].Values[0]);
        Assert.Equal(100.0, capturedResult.Rows[0].Values[1]);
        Assert.Null(capturedResult.Rows[0].RtId);

        // Second group
        Assert.Equal("Inactive", capturedResult.Rows[1].Values[0]);
        Assert.Equal(50.0, capturedResult.Rows[1].Values[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_AppliesInMemorySkip()
    {
        var config = CreateConfig(skip: 1);
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, QueryResult qr) =>
                capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        // Skip 1 → 2 rows remaining (B and C)
        Assert.Equal(2, capturedResult!.Rows.Count);
        Assert.Equal("B", capturedResult.Rows[0].Values[0]);
        Assert.Equal("C", capturedResult.Rows[1].Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_AppliesInMemoryTake()
    {
        var config = CreateConfig(take: 2);
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        A.CallTo(() => dataContext.SetValueByPath(
                "$.queryResult",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, QueryResult qr) =>
                capturedResult = qr);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedResult);
        // Take 2 → only A and B
        Assert.Equal(2, capturedResult!.Rows.Count);
        Assert.Equal("A", capturedResult.Rows[0].Values[0]);
        Assert.Equal("B", capturedResult.Rows[1].Values[0]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithGroupedAggregationQuery_NullResult_DoesNotCallNext()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var groupedQuery = CreateTestGroupedAggregationQuery(
            ["status"], "quantity", RtAggregationTypesEnum.Sum);
        SetupGroupingAggregationQuery(groupedQuery);

        // No field aggregation result
        SetupGraphByTypeResult(CreateEmptyGraphResultSet(fieldAggregationResult: null));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    #endregion

    #region Transaction Tests

    [Fact]
    public async Task ProcessObjectAsync_WithAggregationQuery_StartsAndCommitsTransaction()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);

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

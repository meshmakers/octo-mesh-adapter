using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetRtEntitiesByIdNodeTests
{
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public GetRtEntitiesByIdNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        GetRtEntitiesByIdNodeConfiguration config, JToken? testData = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            "GetRtEntitiesById",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();

        return (dataContext, nodeContext, next);
    }

    private GetRtEntitiesByIdNode CreateNode(NodeDelegate next)
    {
        return new GetRtEntitiesByIdNode(next, _etlContext);
    }

    private static IResultSet<RtEntity> CreateResultSet(params RtEntity[] entities)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Length);
        return resultSet;
    }

    private void SetupGetRtEntitiesById(IResultSet<RtEntity> resultSet)
    {
        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(resultSet);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithRtIds_QueriesRepository()
    {
        var rtIds = new List<OctoObjectId> { new("000000000000000000000001") };
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = rtIds,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var entity = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        SetupGetRtEntitiesById(CreateResultSet(entity));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                TestCkTypeId,
                A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Count == 1),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithRtIds_SetsResultOnDataContext()
    {
        var rtIds = new List<OctoObjectId> { new("000000000000000000000001") };
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = rtIds,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var entity = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        SetupGetRtEntitiesById(CreateResultSet(entity));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.result",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<IResultSet<RtEntity>>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithRtIds_CallsNext()
    {
        var rtIds = new List<OctoObjectId> { new("000000000000000000000001") };
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = rtIds,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithRtIdsPath_ResolvesIdsFromDataContext()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIdsPath = "$.ids",
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleArrayValueByPath<string>("$.ids"))
            .Returns(new List<string> { "000000000000000000000001", "000000000000000000000002" });

        var entity1 = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        var entity2 = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000002"));
        SetupGetRtEntitiesById(CreateResultSet(entity1, entity2));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                TestCkTypeId,
                A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Count == 2),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoRtIdsAndNoRtIdsPath_DoesNotCallNext()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
            // RtIds and RtIdsPath are both null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyRtIds_DoesNotCallNext()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = new List<OctoObjectId>(),
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeIdPath_ResolvesCkTypeIdFromDataContext()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeIdPath = "$.typeId",
            RtIds = new List<OctoObjectId> { new("000000000000000000000001") },
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.typeId"))
            .Returns("TestModel/ResolvedType");

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                new RtCkId<CkTypeId>("TestModel/ResolvedType"),
                A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoCkTypeIdAndNoCkTypeIdPath_Throws()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            RtIds = new List<OctoObjectId> { new("000000000000000000000001") },
            TargetPath = "$.result"
            // CkTypeId and CkTypeIdPath are both null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithRtIdsPath_EmptyIdsAtPath_Throws()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIdsPath = "$.ids",
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleArrayValueByPath<string>("$.ids"))
            .Returns(new List<string>());

        var node = CreateNode(next);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSkipAndTake_PassesValuesToRepository()
    {
        var rtIds = new List<OctoObjectId> { new("000000000000000000000001") };
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = rtIds,
            TargetPath = "$.result",
            Skip = 5,
            Take = 10
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                TestCkTypeId,
                A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._,
                5,
                10))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_StartsAndCommitsTransaction()
    {
        var rtIds = new List<OctoObjectId> { new("000000000000000000000001") };
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            RtIds = rtIds,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }
}

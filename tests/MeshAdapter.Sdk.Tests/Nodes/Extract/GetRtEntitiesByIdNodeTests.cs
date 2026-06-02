using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
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

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetRtEntitiesByIdNodeTests : NodeTestBase
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

    private static void SetupGetArray(IDataContext dataContext, string path, IEnumerable<string?>? values)
    {
        A.CallTo(() => dataContext.GetArray<string>(path)).Returns(values);
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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        var entity = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        SetupGetRtEntitiesById(CreateResultSet(entity));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.result",
                A<IResultSet<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        SetupGetArray(dataContext, "$.ids",
            new List<string?> { "000000000000000000000001", "000000000000000000000002" });

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
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        SetupGetSimpleValueByPath(dataContext, "$.typeId", "TestModel/ResolvedType");

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
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        SetupGetArray(dataContext, "$.ids", new List<string?>());

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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByIdNodeConfiguration>(config);

        SetupGetRtEntitiesById(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }
}

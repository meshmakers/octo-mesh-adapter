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

public class GetRtEntitiesByTypeNodeTests : NodeTestBase
{
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public GetRtEntitiesByTypeNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private GetRtEntitiesByTypeNode CreateNode(NodeDelegate next)
    {
        return new GetRtEntitiesByTypeNode(next, _etlContext);
    }

    private static IResultSet<RtEntity> CreateResultSet(params RtEntity[] entities)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Length);
        return resultSet;
    }

    private void SetupGetRtEntitiesByType(IResultSet<RtEntity> resultSet)
    {
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(resultSet);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeId_QueriesRepository()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        var entity = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        SetupGetRtEntitiesByType(CreateResultSet(entity));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                TestCkTypeId,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeId_SetsResultOnDataContext()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        var entity = new RtEntity(TestCkTypeId, new OctoObjectId("000000000000000000000001"));
        SetupGetRtEntitiesByType(CreateResultSet(entity));

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
    public async Task ProcessObjectAsync_CallsNext()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        SetupGetRtEntitiesByType(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeIdPath_ResolvesCkTypeIdFromDataContext()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeIdPath = "$.typeId",
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        SetupGetSimpleValueByPath(dataContext, "$.typeId", "TestModel/ResolvedType");
        SetupGetRtEntitiesByType(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                new RtCkId<CkTypeId>("TestModel/ResolvedType"),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoCkTypeIdAndNoCkTypeIdPath_Throws()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSkipAndTake_PassesValuesToRepository()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result",
            Skip = 10,
            Take = 20
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        SetupGetRtEntitiesByType(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                TestCkTypeId,
                A<RtEntityQueryOptions>._,
                10,
                20))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_StartsAndCommitsTransaction()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        SetupGetRtEntitiesByType(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyResult_StillSetsValueAndCallsNext()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetRtEntitiesByTypeNodeConfiguration>(config);

        SetupGetRtEntitiesByType(CreateResultSet());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.result",
                A<IResultSet<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();

        VerifyNextCalled(next, dataContext, nodeContext);
    }
}

using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SaveInTimeSeriesNodeTests
{
    private const string TenantId = "test-tenant";
    private const string DataPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;

    public SaveInTimeSeriesNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => _etlContext.TenantId).Returns(TenantId);
        A.CallTo(() => _etlContext.TransactionStartedDateTime).Returns(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        SaveInTimeSeriesNodeConfiguration config, JToken? testData = null)
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
            "SaveInTimeSeries",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();

        return (dataContext, nodeContext, next);
    }

    private SaveInTimeSeriesNode CreateNode(NodeDelegate next)
    {
        return new SaveInTimeSeriesNode(next, _etlContext, _systemContext);
    }

    private static RtEntity CreateRtEntity(DateTime? changedDateTime = null, string? wellKnownName = null)
    {
        var ckTypeId = new RtCkId<CkTypeId>("TestModel/TestType");
        var rtId = new OctoObjectId("000000000000000000000001");
        var entity = new RtEntity(ckTypeId, rtId)
        {
            RtChangedDateTime = changedDateTime,
            RtWellKnownName = wellKnownName
        };
        return entity;
    }

    private static EntityUpdateInfo<RtEntity> CreateInsertUpdateInfo(RtEntity? rtEntity = null)
    {
        var entity = rtEntity ?? CreateRtEntity(DateTime.UtcNow);
        return EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("TestModel/TestType"), entity);
    }

    private static EntityUpdateInfo<RtEntity> CreateUpdateUpdateInfo(RtEntity? rtEntity = null)
    {
        var entity = rtEntity ?? CreateRtEntity(DateTime.UtcNow);
        var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>("TestModel/TestType"), entity.RtId);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, entity);
    }

    private static EntityUpdateInfo<RtEntity> CreateDeleteUpdateInfo()
    {
        var rtEntityId = new RtEntityId(
            new RtCkId<CkTypeId>("TestModel/TestType"),
            new OctoObjectId("000000000000000000000099"));
        return EntityUpdateInfo<RtEntity>.CreateDelete(rtEntityId);
    }

    private void SetupDataContext(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(path, A<JsonSerializer>._))
            .Returns(data);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsEnsureDatabaseCreated()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.EnsureDatabaseCreatedAsync())
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInsertData_InsertsDataPoints()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo(),
            CreateInsertUpdateInfo()
        };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.InsertAsync(
                ArchiveRtId,
                A<IEnumerable<StreamDataPoint>>.That.Matches(d => d.Count() == 2)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUpdateData_InsertsDataPoints()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateUpdateUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.InsertAsync(
                ArchiveRtId,
                A<IEnumerable<StreamDataPoint>>.That.Matches(d => d.Count() == 1)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDeleteModOption_DoesNotInsert()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateDeleteUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_DoesNotCreateTable()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.EnsureDatabaseCreatedAsync())
            .MustNotHaveHappened();
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotCreateTable()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, new List<EntityUpdateInfo<RtEntity>>());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.EnsureDatabaseCreatedAsync())
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_CallsNext()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullRtEntity_SkipsDataPoint()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo(),
            CreateDeleteUpdateInfo() // Has null RtEntity, won't pass the switch (Delete mod option)
        };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Only 1 insert should happen (the valid one), the delete is skipped by mod option
        A.CallTo(() => _streamDataRepository.InsertAsync(
                ArchiveRtId,
                A<IEnumerable<StreamDataPoint>>.That.Matches(d => d.Count() == 1)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_UsesRtChangedDateTimeAsTimestamp()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var changedDateTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var entity = CreateRtEntity(changedDateTime);
        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo(entity) };
        SetupDataContext(dataContext, DataPath, data);

        IEnumerable<StreamDataPoint>? capturedDataPoints = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> dps) => capturedDataPoints = dps.ToList());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedDataPoints);
        var dp = capturedDataPoints!.Single();
        Assert.Equal(changedDateTime, dp.Timestamp);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoRtChangedDateTime_FallsBackToTransactionDateTime()
    {
        var config = new SaveInTimeSeriesNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var entity = CreateRtEntity(changedDateTime: null);
        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo(entity) };
        SetupDataContext(dataContext, DataPath, data);

        IEnumerable<StreamDataPoint>? capturedDataPoints = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> dps) => capturedDataPoints = dps.ToList());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedDataPoints);
        var dp = capturedDataPoints!.Single();
        Assert.Equal(_etlContext.TransactionStartedDateTime, dp.Timestamp);
    }
}

using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class BackfillFromRtEntityNodeTests
{
    private const string TenantId = "test-tenant";
    private const string DataPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;
    private readonly IArchiveRuntimeStore _archiveStore;

    public BackfillFromRtEntityNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();
        _archiveStore = A.Fake<IArchiveRuntimeStore>();

        A.CallTo(() => _etlContext.TenantId).Returns(TenantId);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        BackfillFromRtEntityNodeConfiguration config)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(), logger, dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode("BackfillFromRtEntity", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private void ConfigureArchive(params (string Path, bool Indexed, bool Required)[] columns)
    {
        var snapshot = new ArchiveSnapshot(
            ArchiveRtId,
            TestCkTypeId,
            CkArchiveStatus.Activated,
            "TestArchive",
            columns.Select(c => new CkArchiveColumnSpec(c.Path, c.Indexed, c.Required)).ToArray());
        A.CallTo(() => _archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(snapshot));
    }

    private static EntityUpdateInfo<RtEntity> CreateUpdateInfo(OctoObjectId rtId,
        params (string Name, AttributeValueTypesDto Type, object Value)[] preset)
    {
        var entity = new RtEntity(TestCkTypeId, rtId);
        foreach (var (name, type, value) in preset)
        {
            entity.SetAttributeValue(name, type, value);
        }
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkTypeId, rtId), entity);
    }

    private void StubPersistedEntity(OctoObjectId rtId,
        params (string Name, AttributeValueTypesDto Type, object Value)[] attributes)
    {
        var entity = new RtEntity(TestCkTypeId, rtId);
        foreach (var (name, type, value) in attributes)
        {
            entity.SetAttributeValue(name, type, value);
        }
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync(_session, A<RtEntityId>.That.Matches(id => id.RtId == rtId)))
            .Returns(Task.FromResult<RtEntity?>(entity));
    }

    [Fact]
    public async Task ProcessObjectAsync_FillsMissingAttributesFromPersistentEntity()
    {
        ConfigureArchive(("Voltage", false, false), ("Current", false, false));

        var rtId = new OctoObjectId("000000000000000000000001");
        var update = CreateUpdateInfo(rtId, ("Voltage", AttributeValueTypesDto.Double, 230.0));
        StubPersistedEntity(rtId, ("Current", AttributeValueTypesDto.Double, 5.5));

        var data = new List<EntityUpdateInfo<RtEntity>> { update };
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(230.0, update.RtEntity!.GetAttributeValueOrDefault<double>("Voltage"));
        Assert.Equal(5.5, update.RtEntity!.GetAttributeValueOrDefault<double>("Current"));
    }

    [Fact]
    public async Task ProcessObjectAsync_DoesNotOverwriteExistingValues()
    {
        ConfigureArchive(("Voltage", false, false));

        var rtId = new OctoObjectId("000000000000000000000002");
        var update = CreateUpdateInfo(rtId, ("Voltage", AttributeValueTypesDto.Double, 230.0));
        StubPersistedEntity(rtId, ("Voltage", AttributeValueTypesDto.Double, 999.0));

        var data = new List<EntityUpdateInfo<RtEntity>> { update };
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(230.0, update.RtEntity!.GetAttributeValueOrDefault<double>("Voltage"));
        // The Mongo round-trip should be skipped entirely when nothing is missing.
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._, A<RtEntityId>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_CachesRepeatedRtIds()
    {
        ConfigureArchive(("Voltage", false, false), ("Current", false, false));

        var rtId = new OctoObjectId("000000000000000000000003");
        var u1 = CreateUpdateInfo(rtId, ("Voltage", AttributeValueTypesDto.Double, 230.0));
        var u2 = CreateUpdateInfo(rtId, ("Current", AttributeValueTypesDto.Double, 5.0));
        StubPersistedEntity(rtId,
            ("Voltage", AttributeValueTypesDto.Double, 999.0),
            ("Current", AttributeValueTypesDto.Double, 888.0));

        var data = new List<EntityUpdateInfo<RtEntity>> { u1, u2 };
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Same RtId across two update infos — only one Mongo lookup expected.
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._, A<RtEntityId>._))
            .MustHaveHappenedOnceExactly();
        Assert.Equal(888.0, u1.RtEntity!.GetAttributeValueOrDefault<double>("Current"));
        Assert.Equal(999.0, u2.RtEntity!.GetAttributeValueOrDefault<double>("Voltage"));
    }

    [Fact]
    public async Task ProcessObjectAsync_NoUpdateInfos_PassesThrough()
    {
        ConfigureArchive(("Voltage", false, false));
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(null);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._)).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_ArchiveNotFound_Throws()
    {
        A.CallTo(() => _archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(null));

        var rtId = new OctoObjectId("000000000000000000000004");
        var data = new List<EntityUpdateInfo<RtEntity>> { CreateUpdateInfo(rtId) };
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_PersistedEntityNotFound_LeavesUpdateAlone()
    {
        ConfigureArchive(("Voltage", false, false));

        var rtId = new OctoObjectId("000000000000000000000005");
        var update = CreateUpdateInfo(rtId);
        A.CallTo(() => _tenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._, A<RtEntityId>._))
            .Returns(Task.FromResult<RtEntity?>(null));

        var data = new List<EntityUpdateInfo<RtEntity>> { update };
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString
        });
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);

        var node = new BackfillFromRtEntityNode(next, _etlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.False(update.RtEntity!.Attributes.ContainsKey("Voltage"));
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

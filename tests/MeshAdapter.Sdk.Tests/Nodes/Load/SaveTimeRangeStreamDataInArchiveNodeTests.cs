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

// Pins the time-range save node's wiring through to IStreamDataRepository.InsertTimeRangeAsync.
// Critical behaviours: From/To stripped from attributes (so they don't reappear as user columns),
// entities without a usable window are skipped (not aborted), deletes are not propagated.
public class SaveTimeRangeStreamDataInArchiveNodeTests
{
    private const string TenantId = "test-tenant";
    private const string DataPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;

    public SaveTimeRangeStreamDataInArchiveNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => _etlContext.TenantId).Returns(TenantId);

        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        SaveTimeRangeStreamDataInArchiveNodeConfiguration config)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(
            "SaveTimeRangeStreamDataInArchive", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private static RtEntity CreateEntityWithWindow(DateTime? from, DateTime? to, string fromKey = "From", string toKey = "To")
    {
        var ckTypeId = new RtCkId<CkTypeId>("Industry.Energy/Meter");
        var rtId = new OctoObjectId("000000000000000000000001");
        var entity = new RtEntity(ckTypeId, rtId);
        if (from is not null) entity.SetAttributeRawValue(fromKey, from.Value);
        if (to is not null) entity.SetAttributeRawValue(toKey, to.Value);
        entity.SetAttributeRawValue("energyConsumed", 42.3);
        return entity;
    }

    private static EntityUpdateInfo<RtEntity> CreateInsert(RtEntity entity)
    {
        return EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("Industry.Energy/Meter"), entity);
    }

    private void SetupDataContext(IDataContext dataContext, List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(DataPath, A<JsonSerializer>._))
            .Returns(data);
    }

    [Fact]
    public async Task Insert_WithValidWindow_ForwardsToInsertTimeRangeAsync_WithStrippedWindowAttributes()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var from = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc);
        var entity = CreateEntityWithWindow(from, to);
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        List<TimeRangeStreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.ToList());

        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal(from, captured[0].From);
        Assert.Equal(to, captured[0].To);
        // The user-attribute dictionary must NOT contain the window keys — they've been promoted
        // to first-class fields. Surviving entries are the genuine user columns.
        Assert.False(captured[0].Attributes.ContainsKey("From"));
        Assert.False(captured[0].Attributes.ContainsKey("To"));
        Assert.True(captured[0].Attributes.ContainsKey("energyConsumed"));
    }

    [Fact]
    public async Task Insert_WithoutWindow_SkipsRow_DoesNotCallInsert()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var entity = CreateEntityWithWindow(from: null, to: null);
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        // Nothing usable in the batch → InsertTimeRangeAsync is not called.
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ConfigurableAttributePaths_HonouredWhenSet()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath,
            ArchiveRtId = ArchiveRtIdString,
            FromAttributePath = "windowStart",
            ToAttributePath = "windowEnd",
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var from = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc);
        var entity = CreateEntityWithWindow(from, to, fromKey: "windowStart", toKey: "windowEnd");
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        TimeRangeStreamDataPoint? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.Single());

        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        Assert.Equal(from, captured!.From);
        Assert.Equal(to, captured.To);
        Assert.False(captured.Attributes.ContainsKey("windowStart"));
        Assert.False(captured.Attributes.ContainsKey("windowEnd"));
    }

    [Fact]
    public async Task Delete_IsNotPropagated_AsTimeRangeArchives_DoNotSupportDeletes()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>("Industry.Energy/Meter"),
            new OctoObjectId("000000000000000000000099"));
        var del = EntityUpdateInfo<RtEntity>.CreateDelete(rtEntityId);
        SetupDataContext(dataContext, new() { del });

        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task NullData_LogsWarning_AndCallsNext()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupDataContext(dataContext, null);

        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.EnsureDatabaseCreatedAsync()).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappened();
    }

    [Fact]
    public async Task EmptyArchiveRtId_Throws_BeforeAnyRepoCall()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = "" };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
                .ProcessObjectAsync(dataContext, nodeContext));
    }
}

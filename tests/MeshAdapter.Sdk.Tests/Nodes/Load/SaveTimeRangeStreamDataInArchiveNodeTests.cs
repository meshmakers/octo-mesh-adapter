using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

// Pins the time-range save node's wiring through to IStreamDataRepository.InsertTimeRangeAsync.
// Critical behaviours: From/To stripped from attributes (so they don't reappear as user columns),
// entities without a usable window are skipped (not aborted), deletes are not propagated.
public class SaveTimeRangeStreamDataInArchiveNodeTests : NodeTestBase
{
    private const string TenantId = "test-tenant";
    private const string DataPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);
    private const string SourceRtIdString = "000000000000000000000001";

    private readonly IMeshEtlContext _etlContext;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public SaveTimeRangeStreamDataInArchiveNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => _etlContext.TenantId).Returns(TenantId);

        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));

        // By default every source rtId is treated as existing in the rt-model so the existing
        // behavioural tests are unaffected by the integrity guard. Individual tests override this.
        SetupExistingRtIds(new OctoObjectId(SourceRtIdString));
    }

    /// <summary>
    /// Stubs the by-id existence check (<c>GetRtEntitiesByIdAsync(session, ckTypeId, rtIds, …)</c>)
    /// to return exactly the supplied rtIds as existing entities.
    /// </summary>
    private void SetupExistingRtIds(params OctoObjectId[] existingRtIds)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        var entities = existingRtIds
            .Select(id => new RtEntity(new RtCkId<CkTypeId>("Industry.Energy/Meter"), id))
            .ToList();
        A.CallTo(() => resultSet.Items).Returns(entities);
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Count);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));
    }

    private static RtEntity CreateEntityWithWindow(DateTime? from, DateTime? to, string fromKey = "From", string toKey = "To")
    {
        var ckTypeId = new RtCkId<CkTypeId>("Industry.Energy/Meter");
        var rtId = new OctoObjectId(SourceRtIdString);
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
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(DataPath))
            .Returns(data);
    }

    [Fact]
    public async Task Insert_WithValidWindow_ForwardsToInsertTimeRangeAsync_WithStrippedWindowAttributes()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

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
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
                .ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task IntegrityGuard_AllSourceRtIdsExist_InsertsNormally()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

        var from = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc);
        var entity = CreateEntityWithWindow(from, to);
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        // The source rtId (SourceRtIdString) is registered as existing by the ctor default.
        await new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IntegrityGuard_SourceRtIdMissing_Throws_AndDoesNotInsert()
    {
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);

        var from = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc);
        var entity = CreateEntityWithWindow(from, to);
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        // The rt-model no longer contains the source rtId → guard must fail loudly.
        SetupExistingRtIds(/* none exist */);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaveTimeRangeStreamDataInArchiveNode(next, _etlContext, _systemContext)
                .ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains(SourceRtIdString, ex.Message);
        Assert.Contains(ArchiveRtIdString, ex.Message);

        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}

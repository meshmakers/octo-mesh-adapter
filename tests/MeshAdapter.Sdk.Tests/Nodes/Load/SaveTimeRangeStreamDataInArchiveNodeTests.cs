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
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

// Pins the time-range save node's wiring through to IStreamDataRepository.InsertTimeRangeAsync.
// Critical behaviours: From/To stripped from attributes (so they don't reappear as user columns),
// entities without a usable window are skipped (not aborted), deletes are not propagated.
public class SaveTimeRangeStreamDataInArchiveNodeTests : SessionNodeTestBase
{
    private const string TenantId = "test-tenant";
    private const string DataPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);
    private const string SourceRtIdString = "000000000000000000000001";

    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _streamDataRepository;

    public SaveTimeRangeStreamDataInArchiveNodeTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(TenantId);

        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();
        _streamDataRepository = A.Fake<IStreamDataRepository>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(_tenantContext));
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
        GivenSystemSessionIsExpected();

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

        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(
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

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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
            new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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
        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
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
            new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
                .ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains(SourceRtIdString, ex.Message);
        Assert.Contains(ArchiveRtIdString, ex.Message);

        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // ---- AB#7596: origin, writer and reason -----------------------------------------------------------------

    private async Task<List<TimeRangeStreamDataPoint>?> RunWithOriginAsync(
        SaveTimeRangeStreamDataInArchiveNodeConfiguration config, VerifiedPrincipal? caller, Action<RtEntity>? shape = null)
    {
        A.CallTo(() => EtlContext.VerifiedPrincipal).Returns(caller);
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
        var entity = CreateEntityWithWindow(new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc));
        shape?.Invoke(entity);
        SetupDataContext(dataContext, new() { CreateInsert(entity) });

        List<TimeRangeStreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.ToList());

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);
        return captured;
    }

    private static VerifiedPrincipal Caller() => new("user-rt-1", "acme", "u@example.com", "u", ["Reader"], null);

    [Fact]
    public async Task WithoutOrigin_WritesAutomatedRowsWithoutWriter_AsBefore()
    {
        var captured = await RunWithOriginAsync(
            new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString }, Caller());

        var point = Assert.Single(captured!);
        Assert.Equal(ArchiveRowOrigin.Automated, point.Origin);
        Assert.Null(point.ManualWrittenBy);
    }

    [Fact]
    public async Task OriginManual_RecordsTheVerifiedCallerAndTheStaticReason()
    {
        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual, ManualReason = "meter exchanged"
        }, Caller());

        var point = Assert.Single(captured!);
        Assert.Equal(ArchiveRowOrigin.Manual, point.Origin);
        Assert.Equal("u", point.ManualWrittenBy);
        Assert.Equal("meter exchanged", point.ManualReason);
    }

    [Fact]
    public async Task OriginManual_RecordsTheReadableNameOfTheCaller_NotTheSubjectId()
    {
        // AB#7647: the writer is the verified caller's readable name, the opaque subject id only without one.
        var named = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, new VerifiedPrincipal("8a4c1f00-0000-4000-8000-000000000001", "acme", "jane@example.com", "Jane Doe", ["Writer"], null));
        Assert.Equal("Jane Doe", Assert.Single(named!).ManualWrittenBy);

        var unnamed = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, new VerifiedPrincipal("8a4c1f00-0000-4000-8000-000000000001", "acme", null, " ", ["Writer"], null));
        Assert.Equal("8a4c1f00-0000-4000-8000-000000000001", Assert.Single(unnamed!).ManualWrittenBy);
    }

    [Fact]
    public async Task OriginManual_AClientCredentialsCaller_KeepsItsClientId()
    {
        // The HTTP trigger puts a client-credentials caller's client id into SubjectId and has no name for it.
        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, new VerifiedPrincipal("import-service", "acme", null, null, [], null));

        Assert.Equal("import-service", Assert.Single(captured!).ManualWrittenBy);
    }

    [Fact]
    public async Task OriginManual_APerRowReasonWinsAndIsNoUserColumn()
    {
        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual,
            ManualReason = "static", ManualReasonAttributePath = "Reason"
        }, Caller(), e => e.SetAttributeRawValue("Reason", "per row"));

        var point = Assert.Single(captured!);
        Assert.Equal("per row", point.ManualReason);
        Assert.False(point.Attributes.ContainsKey("Reason"));
    }

    [Fact]
    public async Task OriginManual_WithoutAUserCaller_RecordsTheServiceIdentity()
    {
        // A file-based manual import runs without a user caller: the writer is the client id of the service
        // identity the pipeline runs under, never refused (AB#7596).
        A.CallTo(() => EtlContext.GetServiceIdentitySubjectIdAsync()).Returns(new ValueTask<string?>("octo-pipeline-sa-1"));

        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, caller: null);

        var point = Assert.Single(captured!);
        Assert.Equal(ArchiveRowOrigin.Manual, point.Origin);
        Assert.Equal("octo-pipeline-sa-1", point.ManualWrittenBy);
    }

    [Fact]
    public async Task OriginManual_AUserCallerWinsOverTheServiceIdentity()
    {
        A.CallTo(() => EtlContext.GetServiceIdentitySubjectIdAsync()).Returns(new ValueTask<string?>("octo-pipeline-sa-1"));

        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, Caller());

        Assert.Equal("u", Assert.Single(captured!).ManualWrittenBy);
    }

    [Fact]
    public async Task OriginManual_WithoutCallerOrServiceIdentity_RecordsThePipeline_NeverEmpty()
    {
        A.CallTo(() => EtlContext.GetServiceIdentitySubjectIdAsync()).Returns(new ValueTask<string?>((string?)null));
        A.CallTo(() => EtlContext.PipelineRtEntityId).Returns(new RtEntityId("System/RtDataPipeline", new OctoObjectId("000000000000000000000099")));

        var captured = await RunWithOriginAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual
        }, caller: null);

        Assert.Equal("pipeline:000000000000000000000099", Assert.Single(captured!).ManualWrittenBy);
    }

    [Fact]
    public async Task AReasonWithoutOriginManual_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunWithOriginAsync(
            new SaveTimeRangeStreamDataInArchiveNodeConfiguration
            {
                Path = DataPath, ArchiveRtId = ArchiveRtIdString, ManualReason = "why"
            }, Caller()));
    }

    // ---- AB#7638: a per-row reason read from the DataContext item ----------------------------------------------

    private async Task<List<TimeRangeStreamDataPoint>?> RunWithItemReasonsAsync(
        SaveTimeRangeStreamDataInArchiveNodeConfiguration config, params string?[] itemReasons)
    {
        A.CallTo(() => EtlContext.VerifiedPrincipal).Returns(Caller());
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
        var data = new List<EntityUpdateInfo<RtEntity>>();
        for (var i = 0; i < itemReasons.Length; i++)
        {
            var from = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc).AddMinutes(15 * i);
            var entity = CreateEntityWithWindow(from, from.AddMinutes(15));
            entity.SetAttributeRawValue("Reason", "from the attribute");
            data.Add(CreateInsert(entity));

            var item = A.Fake<IDataContext>();
            var reason = itemReasons[i];
            string? ignored;
            A.CallTo(() => item.TryGet("$.manualReason", out ignored))
                .Returns(reason != null)
                .AssignsOutAndRefParameters(reason);
            A.CallTo(() => dataContext.Select($"{DataPath}[{i}]")).Returns(item);
        }

        SetupDataContext(dataContext, data);

        List<TimeRangeStreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.ToList());

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);
        return captured;
    }

    [Fact]
    public async Task OriginManual_APerItemReasonFromTheDataContext_IsStoredPerRow()
    {
        var captured = await RunWithItemReasonsAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual,
            ManualReason = "static", ManualReasonPath = "$.manualReason"
        }, "meter exchanged", "reading corrected");

        Assert.Equal(["meter exchanged", "reading corrected"], captured!.Select(p => p.ManualReason));
        Assert.All(captured!, p => Assert.Equal(ArchiveRowOrigin.Manual, p.Origin));
    }

    [Fact]
    public async Task OriginManual_AnItemWithoutAReason_FallsBackToTheAttributeThenTheStaticReason()
    {
        var withAttribute = await RunWithItemReasonsAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual,
            ManualReason = "static", ManualReasonPath = "$.manualReason", ManualReasonAttributePath = "Reason"
        }, null, "  ");
        Assert.Equal(["from the attribute", "from the attribute"], withAttribute!.Select(p => p.ManualReason));
        Assert.All(withAttribute!, p => Assert.False(p.Attributes.ContainsKey("Reason")));

        var staticOnly = await RunWithItemReasonsAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual,
            ManualReason = "static", ManualReasonPath = "$.manualReason"
        }, (string?)null);
        Assert.Equal("static", Assert.Single(staticOnly!).ManualReason);
    }

    [Fact]
    public async Task OriginManual_APerItemReasonWinsOverTheAttributeReason()
    {
        var captured = await RunWithItemReasonsAsync(new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, Origin = ArchiveWriteOrigin.Manual,
            ManualReasonPath = "$.manualReason", ManualReasonAttributePath = "Reason"
        }, "from the item");

        Assert.Equal("from the item", Assert.Single(captured!).ManualReason);
    }

    [Fact]
    public async Task APerItemReasonPathWithoutOriginManual_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunWithItemReasonsAsync(
            new SaveTimeRangeStreamDataInArchiveNodeConfiguration
            {
                Path = DataPath, ArchiveRtId = ArchiveRtIdString, ManualReasonPath = "$.manualReason"
            }, "why"));
        Assert.Contains("only recorded for origin Manual", ex.Message);
    }

    // ---- AB#7598: the write result ---------------------------------------------------------------------------

    private async Task<(IDataContext DataContext, INodeContext NodeContext)> RunWithResultAsync(
        SaveTimeRangeStreamDataInArchiveNodeConfiguration config, StreamDataWriteResult result)
    {
        A.CallTo(() => EtlContext.VerifiedPrincipal).Returns(null);
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
        SetupDataContext(dataContext, new()
        {
            CreateInsert(CreateEntityWithWindow(new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc)))
        });
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Returns(result);

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);
        return (dataContext, nodeContext);
    }

    private static StreamDataWriteResult OneProtectedRow() => new(0,
    [
        new StreamDataSkippedRow(new OctoObjectId(SourceRtIdString), new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc), ArchiveRowOrigin.Automated, StreamDataSkipReason.ManualValueProtected)
    ]);

    [Fact]
    public async Task SkippedRows_AreReportedAsAWarningWithTheReason()
    {
        A.CallTo(() => EtlContext.VerifiedPrincipal).Returns(null);
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration { Path = DataPath, ArchiveRtId = ArchiveRtIdString };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
        SetupDataContext(dataContext, new()
        {
            CreateInsert(CreateEntityWithWindow(new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc)))
        });
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Returns(OneProtectedRow());

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
            A<string>.That.Contains("ManualValueProtected 1"), A<object[]>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FailOnSkippedRows_FailsTheNode()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunWithResultAsync(
            new SaveTimeRangeStreamDataInArchiveNodeConfiguration
            {
                Path = DataPath, ArchiveRtId = ArchiveRtIdString, FailOnSkippedRows = true
            }, OneProtectedRow()));

        Assert.Contains("1 not stored", ex.Message);
    }

    [Fact]
    public async Task WriteResultPath_StoresTheCountsForLaterNodes()
    {
        System.Text.Json.Nodes.JsonObject? written = null;
        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = DataPath, ArchiveRtId = ArchiveRtIdString, WriteResultPath = "$.writeResult"
        };
        A.CallTo(() => EtlContext.VerifiedPrincipal).Returns(null);
        var (dataContext, nodeContext, next) = PrepareTest<SaveTimeRangeStreamDataInArchiveNodeConfiguration>(config);
        SetupDataContext(dataContext, new()
        {
            CreateInsert(CreateEntityWithWindow(new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 12, 13, 15, 0, DateTimeKind.Utc)))
        });
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Returns(OneProtectedRow());
        A.CallTo(() => dataContext.Set("$.writeResult", A<System.Text.Json.Nodes.JsonObject?>._))
            .Invokes((string _, System.Text.Json.Nodes.JsonObject? value) => written = value);

        await new SaveTimeRangeStreamDataInArchiveNode(next, EtlContext, _systemContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(written);
        Assert.Equal(0, written!["stored"]!.GetValue<int>());
        Assert.Equal(1, written["skipped"]!.GetValue<int>());
        Assert.Equal(1, written["skippedByReason"]!["ManualValueProtected"]!.GetValue<int>());
    }
}

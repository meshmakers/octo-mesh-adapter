using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// Drives <see cref="UpdateRtEntityIfNewerNode"/> through a real <see cref="DataContextImpl"/> (not a
/// mocked <c>IDataContext</c>) so the input/output entities go through the actual STJ serialization
/// round-trip — the path that masked the attribute-dropping blocker. Covers the per-WKN dedup and the
/// <c>ExtractDateTime</c> comparison (including the record-typed <c>TimeRange.From</c> path the energy
/// simulation uses).
/// </summary>
public class UpdateRtEntityIfNewerNodeTests
{
    private const string CkTypeId = "Basic.Energy/EnergyMeasurement";
    private const string Wkn = "MP1-1.8.0";

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public UpdateRtEntityIfNewerNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
        SetupExistingEntities();
    }

    private void SetupExistingEntities(params RtEntity[] existing)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(existing);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));
    }

    private static RtEntity Candidate(DateTime from, int slot)
    {
        var timeRange = new RtRecord(new RtCkId<CkRecordId>("Basic/TimeRange"), new Dictionary<string, object?>
        {
            ["From"] = from,
            ["To"] = from.AddMinutes(15)
        });
        return new RtEntity(new RtCkId<CkTypeId>(CkTypeId), OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?>
            {
                ["TimeRange"] = timeRange,
                ["slot"] = slot
            })
        {
            RtWellKnownName = Wkn
        };
    }

    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareRealContext(
        UpdateRtEntityIfNewerNodeConfiguration config, List<EntityUpdateInfo<RtEntity>> input)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        dataContext.Set(config.InputPath, input);

        var logger = A.Fake<IPipelineLogger>();
        var sp = new ServiceCollection().BuildServiceProvider();
        var root = NodeContext.CreateRootNodeContext(sp, logger, dataContext);
        var nodeContext = root.RegisterChildNode("UpdateRtEntityIfNewer", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task NestedRecordComparisonPath_PicksLatestSlot_NotFirstInBatch()
    {
        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "TimeRange.From"
        };

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Order the batch so the newest slot (slot 3, T+30) is NOT first. With a working
        // comparison, slot 3 wins; if the comparison value can't be read it falls back to the
        // first candidate (slot 2) — the distinguishing assertion below.
        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(Candidate(t1.AddMinutes(15), slot: 2)),
            EntityUpdateInfo<RtEntity>.CreateInsert(Candidate(t1.AddMinutes(30), slot: 3)),
            EntityUpdateInfo<RtEntity>.CreateInsert(Candidate(t1, slot: 1))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered");
        Assert.NotNull(filtered);
        var inserted = Assert.Single(filtered!);
        Assert.NotNull(inserted.RtEntity);
        Assert.Equal(3, inserted.RtEntity!.GetAttributeValue<int>("slot"));

        // Every batch candidate is preserved on the all-output for the archive write.
        var all = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all");
        Assert.Equal(3, all!.Count);
    }

    [Fact]
    public async Task TopLevelComparisonPath_PicksLatestSlot()
    {
        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "measuredAt"
        };

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(TopLevelCandidate(t1.AddMinutes(15), slot: 2)),
            EntityUpdateInfo<RtEntity>.CreateInsert(TopLevelCandidate(t1.AddMinutes(30), slot: 3)),
            EntityUpdateInfo<RtEntity>.CreateInsert(TopLevelCandidate(t1, slot: 1))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var inserted = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Equal(3, inserted.RtEntity!.GetAttributeValue<int>("slot"));
    }

    [Fact]
    public async Task ExistingEntityNewer_CandidateSkippedFromFiltered_ButKeptInAllWithExistingRtId()
    {
        var existingRtId = new OctoObjectId("000000000000000000000abc");
        var existing = new RtEntity(new RtCkId<CkTypeId>(CkTypeId), existingRtId, new Dictionary<string, object?>
        {
            ["TimeRange"] = new RtRecord(new RtCkId<CkRecordId>("Basic/TimeRange"),
                new Dictionary<string, object?> { ["From"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) })
        })
        {
            RtWellKnownName = Wkn
        };
        SetupExistingEntities(existing);

        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "TimeRange.From"
        };

        // Candidate is older than the existing entity → must not reach the RT-write (filtered) path,
        // but must still appear on the all-output carrying the existing RtId for the archive.
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var input = new List<EntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(Candidate(older, slot: 1)) };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);

        var all = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all");
        var kept = Assert.Single(all!);
        Assert.Equal(existingRtId, kept.RtEntity!.RtId);
    }

    [Fact]
    public async Task CandidateWithoutWellKnownName_PassesThroughAsInsertOnBothOutputs()
    {
        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "TimeRange.From"
        };

        var noKey = Candidate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), slot: 1);
        noKey.RtWellKnownName = null;
        var input = new List<EntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(noKey) };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all")!);
    }

    private static RtEntity TopLevelCandidate(DateTime measuredAt, int slot)
    {
        return new RtEntity(new RtCkId<CkTypeId>(CkTypeId), OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?>
            {
                ["measuredAt"] = measuredAt,
                ["slot"] = slot
            })
        {
            RtWellKnownName = Wkn
        };
    }

    private static RtEntity StringComparisonCandidate(string measuredAt, int slot)
    {
        return new RtEntity(new RtCkId<CkTypeId>(CkTypeId), OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?>
            {
                ["measuredAt"] = measuredAt,
                ["slot"] = slot
            })
        {
            RtWellKnownName = Wkn
        };
    }

    [Fact]
    public async Task OffsetBearingDateString_ComparedByUtcInstant_NotWallClock()
    {
        // Pins the offset->UTC handling ExtractDateTime relies on: a date string carrying a
        // non-UTC offset is compared by its UTC instant, not its wall-clock value. Slot 1's wall
        // clock (12:00) is later than slot 2's (09:00), but with the +05:00 offset applied slot 1
        // is 07:00 UTC — EARLIER than slot 2's 09:00 UTC. So the newer entity is slot 2.
        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "measuredAt"
        };

        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(StringComparisonCandidate("2026-01-01T12:00:00+05:00", slot: 1)),
            EntityUpdateInfo<RtEntity>.CreateInsert(StringComparisonCandidate("2026-01-01T09:00:00Z", slot: 2))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var inserted = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Equal(2, inserted.RtEntity!.GetAttributeValue<int>("slot"));
    }

    // ---------------------------------------------------------------------------------------------
    // Scenarios ported from the pre-migration (Newtonsoft) UpdateRtEntityIfNewerNodeTests so the
    // behaviour of fix #16 (typed-record traversal + DateTime kind/offset normalisation) and the
    // per-WKN dedup / association filtering stay covered against the STJ node. The original suite
    // drove a mocked IDataContext with JObject input; these drive the real DataContextImpl instead.
    // Local/DateTimeOffset comparison values are placed on the existing (DB) side so they reach
    // ExtractDateTime as raw CLR values and exercise the terminal kind-normalisation switch.
    // ---------------------------------------------------------------------------------------------

    private static UpdateRtEntityIfNewerNodeConfiguration RecordConfig() => new()
    {
        InputPath = "$._candidates",
        FilteredOutputPath = "$._filtered",
        OutputPathAll = "$._all",
        ComparisonAttributePath = "TimeRange.From"
    };

    private static RtEntity RecordCandidate(OctoObjectId rtId, object fromValue, int slot, string? wkn = Wkn)
    {
        var timeRange = new RtRecord(new RtCkId<CkRecordId>("Basic/TimeRange"), new Dictionary<string, object?>
        {
            ["From"] = fromValue
        });
        return new RtEntity(new RtCkId<CkTypeId>(CkTypeId), rtId,
            new Dictionary<string, object?>
            {
                ["TimeRange"] = timeRange,
                ["slot"] = slot
            })
        {
            RtWellKnownName = wkn
        };
    }

    private static RtEntity RecordExisting(OctoObjectId rtId, object fromValue, string wkn = Wkn)
    {
        var timeRange = new RtRecord(new RtCkId<CkRecordId>("Basic/TimeRange"), new Dictionary<string, object?>
        {
            ["From"] = fromValue
        });
        return new RtEntity(new RtCkId<CkTypeId>(CkTypeId), rtId,
            new Dictionary<string, object?> { ["TimeRange"] = timeRange })
        {
            RtWellKnownName = wkn
        };
    }

    [Fact]
    public async Task TypedRecordPath_DbExistingAndCandidateNewer_UpdatesExistingRtId()
    {
        // Regression for the typed-record traversal fix: TimeRange is an RtRecord, so ExtractDateTime
        // must descend into it. A newer candidate must produce an Update targeting the existing RtId,
        // not the candidate's freshly generated one.
        var existingRtId = new OctoObjectId("000000000000000000000010");
        var existingFrom = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        SetupExistingEntities(RecordExisting(existingRtId, existingFrom));

        var candidate = RecordCandidate(new OctoObjectId("000000000000000000000020"), existingFrom.AddMinutes(15), slot: 1);
        var input = new List<EntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(candidate) };

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Equal(EntityModOptions.Update, filtered.ModOption);
        Assert.Equal(existingRtId, filtered.RtEntity!.RtId);
    }

    [Fact]
    public async Task DateTimeOffsetExisting_NormalisedToUtcInstant_CandidateSameInstantNotNewer()
    {
        // The existing comparison value is a DateTimeOffset for the same instant as the candidate's
        // UTC value. ExtractDateTime must reduce it via dto.UtcDateTime so the candidate is rejected
        // as not-newer (equal instant), then still ride along on the all-output with the existing RtId.
        var existingRtId = new OctoObjectId("000000000000000000000010");
        var instantUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var sameInstantOffset = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.FromHours(2));
        SetupExistingEntities(RecordExisting(existingRtId, sameInstantOffset));

        var candidate = RecordCandidate(new OctoObjectId("000000000000000000000020"), instantUtc, slot: 1);
        var input = new List<EntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(candidate) };

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        var kept = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all")!);
        Assert.Equal(existingRtId, kept.RtEntity!.RtId);
    }

    [Fact]
    public async Task LocalKindExisting_NormalisedToUtcInstant_CandidateSameInstantNotNewer()
    {
        // The existing comparison value is a DateTimeKind.Local DateTime for the same instant as the
        // candidate's UTC value. ExtractDateTime must convert it via ToUniversalTime() (not relabel it
        // with SpecifyKind), so the candidate compares equal and is rejected as not-newer. Holds on any
        // machine time zone because ToLocalTime()->ToUniversalTime() preserves the instant.
        var existingRtId = new OctoObjectId("000000000000000000000010");
        var instantUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var sameInstantLocal = instantUtc.ToLocalTime();
        SetupExistingEntities(RecordExisting(existingRtId, sameInstantLocal));

        var candidate = RecordCandidate(new OctoObjectId("000000000000000000000020"), instantUtc, slot: 1);
        var input = new List<EntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(candidate) };

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
    }

    [Fact]
    public async Task IntraBatchDedup_KeepsLatestAsInsert_AllShareFirstCandidateRtId()
    {
        // Three candidates for the same WKN, no DB match. The latest by TimeRange.From wins on the
        // filtered output as an Insert carrying the FIRST batch candidate's RtId; every candidate
        // appears on the all-output pinned to that same canonical RtId.
        SetupExistingEntities();

        var firstRtId = new OctoObjectId("000000000000000000000020");
        var t0 = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(RecordCandidate(firstRtId, t0, slot: 1)),
            EntityUpdateInfo<RtEntity>.CreateInsert(RecordCandidate(new OctoObjectId("000000000000000000000021"), t0.AddMinutes(30), slot: 3)),
            EntityUpdateInfo<RtEntity>.CreateInsert(RecordCandidate(new OctoObjectId("000000000000000000000022"), t0.AddMinutes(15), slot: 2))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Equal(EntityModOptions.Insert, filtered.ModOption);
        Assert.Equal(firstRtId, filtered.RtEntity!.RtId);
        Assert.Equal(3, filtered.RtEntity!.GetAttributeValue<int>("slot"));

        var all = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all")!;
        Assert.Equal(3, all.Count);
        Assert.All(all, u => Assert.Equal(firstRtId, u.RtEntity!.RtId));
    }

    [Fact]
    public async Task DbMissing_EmitsInsertOnBothOutputs_WithCandidateRtId()
    {
        SetupExistingEntities();

        var candidateRtId = new OctoObjectId("000000000000000000000020");
        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(
                RecordCandidate(candidateRtId, new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc), slot: 1))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Equal(EntityModOptions.Insert, filtered.ModOption);
        Assert.Equal(candidateRtId, filtered.RtEntity!.RtId);

        var all = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all")!);
        Assert.Equal(candidateRtId, all.RtEntity!.RtId);
    }

    [Fact]
    public async Task EmptyInput_WritesEmptyOutputsCallsNextAndSkipsDbQuery()
    {
        var input = new List<EntityUpdateInfo<RtEntity>>();

        var (dataContext, nodeContext, next) = PrepareRealContext(RecordConfig(), input);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._filtered")!);
        Assert.Empty(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$._all")!);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        // Nothing to dedup -> no DB round-trip.
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task AssociationFilter_KeepsOnlyAssociationsForInsertedOrigins()
    {
        // WKN-EXIST already exists in the DB with a newer value; its older candidate is skipped, so its
        // association must be dropped. WKN-NEW has no DB match and becomes an Insert, so its association
        // must survive — keyed on the inserted origin's RtId.
        var existingRtId = new OctoObjectId("000000000000000000000010");
        SetupExistingEntities(
            RecordExisting(existingRtId, new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc), "WKN-EXIST"));

        var config = new UpdateRtEntityIfNewerNodeConfiguration
        {
            InputPath = "$._candidates",
            FilteredOutputPath = "$._filtered",
            OutputPathAll = "$._all",
            ComparisonAttributePath = "TimeRange.From",
            CandidateAssociationsInputPath = "$._assocIn",
            FilteredAssociationsOutputPath = "$._assocOut"
        };

        var newRtId = new OctoObjectId("000000000000000000000020");
        var staleRtId = new OctoObjectId("000000000000000000000021");

        var input = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(
                RecordCandidate(newRtId, new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc), slot: 1, wkn: "WKN-NEW")),
            EntityUpdateInfo<RtEntity>.CreateInsert(
                RecordCandidate(staleRtId, new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc), slot: 1, wkn: "WKN-EXIST"))
        };

        var (dataContext, nodeContext, next) = PrepareRealContext(config, input);

        var ckType = new RtCkId<CkTypeId>(CkTypeId);
        var role = new RtCkId<CkAssociationRoleId>("Basic.Energy/HasMeasurement");
        var assocInput = new List<AssociationUpdateInfo>
        {
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(ckType, newRtId),
                new RtEntityId(ckType, new OctoObjectId("0000000000000000000000aa")),
                role),
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(ckType, staleRtId),
                new RtEntityId(ckType, new OctoObjectId("0000000000000000000000bb")),
                role)
        };
        dataContext.Set("$._assocIn", assocInput);

        await new UpdateRtEntityIfNewerNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        var assocs = dataContext.Get<List<AssociationUpdateInfo>>("$._assocOut");
        Assert.NotNull(assocs);
        var kept = Assert.Single(assocs!);
        Assert.Equal(newRtId, kept.Origin.RtId);
    }
}

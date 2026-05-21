using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class UpdateRtEntityIfNewerNodeTests
{
    private const string InputPath = "$.update.UpdateItems";
    private const string FilteredOutputPath = "$.update.FilteredItems";
    private const string OutputPathAll = "$.update.AllItems";
    private const string AssocInputPath = "$.update.AssocUpdateItems";
    private const string AssocOutputPath = "$.update.FilteredAssocs";

    private static readonly RtCkId<CkTypeId> EmCkType = new("Basic.Energy/EnergyMeasurement");
    private static readonly RtCkId<CkRecordId> TimeRangeRecordId = new("Basic/TimeRange");
    private static readonly RtCkId<CkAssociationRoleId> ParentRole = new("Basic.Energy/HasMeasurement");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;

    public UpdateRtEntityIfNewerNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        var session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(session));
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        UpdateRtEntityIfNewerNodeConfiguration config, JToken? testData = null)
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
            "UpdateRtEntityIfNewer",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private UpdateRtEntityIfNewerNode CreateNode(NodeDelegate next)
        => new(next, _etlContext);

    private static UpdateRtEntityIfNewerNodeConfiguration BaseConfig() => new()
    {
        InputPath = InputPath,
        FilteredOutputPath = FilteredOutputPath,
        OutputPathAll = OutputPathAll,
        ComparisonAttributePath = "TimeRange.To"
    };

    private static RtRecord BuildTimeRange(object toValue, object? fromValue = null)
    {
        var rec = new RtRecord { CkRecordId = TimeRangeRecordId };
        rec.SetAttributeRawValue("To", toValue);
        if (fromValue != null) rec.SetAttributeRawValue("From", fromValue);
        return rec;
    }

    private static RtEntity BuildEmEntity(string rtId, string? wkn, object toValue)
    {
        var entity = new RtEntity(EmCkType, new OctoObjectId(rtId));
        if (!string.IsNullOrEmpty(wkn))
        {
            entity.RtWellKnownName = wkn;
        }
        entity.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, BuildTimeRange(toValue));
        return entity;
    }

    private static EntityUpdateInfo<RtEntity> BuildInsert(string rtId, string? wkn, object toValue)
    {
        var entity = BuildEmEntity(rtId, wkn, toValue);
        return EntityUpdateInfo<RtEntity>.CreateInsert(EmCkType, entity);
    }

    private void SetupExistingByWkn(params RtEntity[] existing)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(existing.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(existing.Length);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }

    private void SetupInput(IDataContext dataContext, List<EntityUpdateInfo<RtEntity>> input)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(
                InputPath, A<JsonSerializer>._))
            .Returns(input);
    }

    private void SetupAssocInput(IDataContext dataContext, List<AssociationUpdateInfo>? input)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<AssociationUpdateInfo>>(
                AssocInputPath, A<JsonSerializer>._))
            .Returns(input);
    }

    /// <summary>
    /// Wires capture of both output paths. Returns lambdas that read the captured lists
    /// after the node has run.
    /// </summary>
    private static (Func<List<EntityUpdateInfo<RtEntity>>?> Filtered,
                    Func<List<EntityUpdateInfo<RtEntity>>?> All) WireOutputCapture(IDataContext dataContext)
    {
        List<EntityUpdateInfo<RtEntity>>? filtered = null;
        List<EntityUpdateInfo<RtEntity>>? all = null;

        A.CallTo(() => dataContext.SetValueByPath(
                FilteredOutputPath,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .Invokes(call => filtered = (List<EntityUpdateInfo<RtEntity>>?)call.Arguments[1]);

        A.CallTo(() => dataContext.SetValueByPath(
                OutputPathAll,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .Invokes(call => all = (List<EntityUpdateInfo<RtEntity>>?)call.Arguments[1]);

        return (() => filtered, () => all);
    }

    private static Func<List<AssociationUpdateInfo>?> WireAssocCapture(IDataContext dataContext)
    {
        List<AssociationUpdateInfo>? captured = null;
        A.CallTo(() => dataContext.SetValueByPath(
                AssocOutputPath,
                A<List<AssociationUpdateInfo>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .Invokes(call => captured = (List<AssociationUpdateInfo>?)call.Arguments[1]);
        return () => captured;
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolvesComparisonValueThroughTypedRtRecord()
    {
        // Regression test for the typed-record traversal fix: TimeRange is an RtRecord, not a
        // dictionary or JObject, so the original ExtractDateTime returned null and treated the
        // candidate as "no comparison value" → skipped.
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, _) = (WireOutputCapture(dataContext));

        var existingTo = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var candidateTo = existingTo.AddMinutes(15);

        var existing = BuildEmEntity("000000000000000000000010", "WKN-1", existingTo);
        SetupExistingByWkn(existing);
        SetupInput(dataContext, [BuildInsert("000000000000000000000020", "WKN-1", candidateTo)]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = getFiltered();
        Assert.NotNull(filtered);
        Assert.Single(filtered);
        Assert.Equal(EntityModOptions.Update, filtered[0].ModOption);
        // Update must target the existing RtId, not the candidate's freshly generated one.
        Assert.Equal(existing.RtId, filtered[0].RtEntity!.RtId);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NormalisesDateTimeOffsetCandidateAgainstUtcExisting()
    {
        // Candidate carries a DateTimeOffset for the same instant as the existing UTC DateTime.
        // Both must normalize to the same moment so the candidate is rejected as not-newer.
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        var instantUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var sameInstantWithOffset = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.FromHours(2));

        var existing = BuildEmEntity("000000000000000000000010", "WKN-1", instantUtc);
        SetupExistingByWkn(existing);
        SetupInput(dataContext, [BuildInsert("000000000000000000000020", "WKN-1", sameInstantWithOffset)]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(getFiltered()!);
        // The candidate still ends up on _allEms with the existing RtId so the archive write lands.
        Assert.Single(getAll()!);
        Assert.Equal(existing.RtId, getAll()![0].RtEntity!.RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_NormalisesLocalKindCandidateAgainstUtcExisting()
    {
        // DateTime with DateTimeKind.Local representing the same instant as a UTC DateTime must
        // compare equal after normalization, not be treated as newer because the numeric ticks
        // happen to be larger on non-UTC machines.
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, _) = WireOutputCapture(dataContext);

        var instantUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var sameInstantLocal = instantUtc.ToLocalTime(); // DateTimeKind.Local

        var existing = BuildEmEntity("000000000000000000000010", "WKN-1", instantUtc);
        SetupExistingByWkn(existing);
        SetupInput(dataContext, [BuildInsert("000000000000000000000020", "WKN-1", sameInstantLocal)]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(getFiltered()!);
    }

    [Fact]
    public async Task ProcessObjectAsync_IntraBatchDedupKeepsLatestAndCanonicalisedRtIds()
    {
        // Three candidates for the same WKN, no DB match. The latest by TimeRange.To wins on
        // _filteredEms as an Insert with the first batch RtId; every candidate appears on
        // _allEms pinned to that same canonical RtId.
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        SetupExistingByWkn(); // no DB match

        var t0 = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var firstRtId = "000000000000000000000020";
        SetupInput(dataContext, [
            BuildInsert(firstRtId, "WKN-1", t0),
            BuildInsert("000000000000000000000021", "WKN-1", t0.AddMinutes(30)),
            BuildInsert("000000000000000000000022", "WKN-1", t0.AddMinutes(15))
        ]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = getFiltered()!;
        var all = getAll()!;

        Assert.Single(filtered);
        Assert.Equal(EntityModOptions.Insert, filtered[0].ModOption);
        Assert.Equal(new OctoObjectId(firstRtId), filtered[0].RtEntity!.RtId);
        // The kept attributes must come from the candidate with the latest TimeRange.To.
        var keptTo = (DateTime)((RtRecord)filtered[0].RtEntity!.Attributes["TimeRange"]!).Attributes["To"]!;
        Assert.Equal(t0.AddMinutes(30), keptTo);

        Assert.Equal(3, all.Count);
        Assert.All(all, u => Assert.Equal(new OctoObjectId(firstRtId), u.RtEntity!.RtId));
    }

    [Fact]
    public async Task ProcessObjectAsync_DbExistsAndCandidateNotNewer_OmitsFromFilteredButKeepsInAll()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        var existingTo = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        var olderTo = existingTo.AddMinutes(-15);

        var existing = BuildEmEntity("000000000000000000000010", "WKN-1", existingTo);
        SetupExistingByWkn(existing);
        SetupInput(dataContext, [BuildInsert("000000000000000000000020", "WKN-1", olderTo)]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(getFiltered()!);
        var all = getAll()!;
        Assert.Single(all);
        Assert.Equal(EntityModOptions.Update, all[0].ModOption);
        Assert.Equal(existing.RtId, all[0].RtEntity!.RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_DbMissing_EmitsInsertOnBothOutputs()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        SetupExistingByWkn();
        var candidateRtId = "000000000000000000000020";
        SetupInput(dataContext,
            [BuildInsert(candidateRtId, "WKN-NEW", new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc))]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var filtered = getFiltered()!;
        Assert.Single(filtered);
        Assert.Equal(EntityModOptions.Insert, filtered[0].ModOption);
        Assert.Equal(new OctoObjectId(candidateRtId), filtered[0].RtEntity!.RtId);

        var all = getAll()!;
        Assert.Single(all);
        Assert.Equal(new OctoObjectId(candidateRtId), all[0].RtEntity!.RtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_CandidateWithoutWellKnownName_PassesThroughAsInsertOnBothOutputs()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        SetupExistingByWkn();
        SetupInput(dataContext,
            [BuildInsert("000000000000000000000020", null, new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc))]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Single(getFiltered()!);
        Assert.Equal(EntityModOptions.Insert, getFiltered()![0].ModOption);
        Assert.Single(getAll()!);
        Assert.Equal(EntityModOptions.Insert, getAll()![0].ModOption);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyInput_WritesEmptyOutputsAndCallsNext()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var (getFiltered, getAll) = WireOutputCapture(dataContext);

        SetupInput(dataContext, new List<EntityUpdateInfo<RtEntity>>());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(getFiltered()!);
        Assert.Empty(getAll()!);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        // No DB query when there is nothing to dedup.
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AssociationFilter_KeepsOnlyAssociationsForInsertedOrigins()
    {
        var config = BaseConfig() with
        {
            CandidateAssociationsInputPath = AssocInputPath,
            FilteredAssociationsOutputPath = AssocOutputPath
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        WireOutputCapture(dataContext);
        var getAssocs = WireAssocCapture(dataContext);

        // WKN-EXIST exists in DB, candidate older → skipped → its association must be dropped.
        // WKN-NEW does not exist → candidate becomes Insert → its association must survive.
        var existing = BuildEmEntity("000000000000000000000010", "WKN-EXIST",
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc));
        SetupExistingByWkn(existing);

        var newRtId = new OctoObjectId("000000000000000000000020");
        var staleRtId = new OctoObjectId("000000000000000000000021");

        SetupInput(dataContext, [
            BuildInsert(newRtId.ToString(), "WKN-NEW",
                new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc)),

            BuildInsert(staleRtId.ToString(), "WKN-EXIST",
                new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc))
        ]);

        SetupAssocInput(dataContext, [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(EmCkType, newRtId),
                new RtEntityId(EmCkType, new OctoObjectId("0000000000000000000000aa")),
                ParentRole),

            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(EmCkType, staleRtId),
                new RtEntityId(EmCkType, new OctoObjectId("0000000000000000000000bb")),
                ParentRole)
        ]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var assocs = getAssocs();
        Assert.NotNull(assocs);
        Assert.Single(assocs);
        Assert.Equal(newRtId, assocs[0].Origin.RtId);
    }
}

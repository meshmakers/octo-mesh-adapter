using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
///     The master-data sync of ApplyChanges@2 (<c>targetTenants</c>, AB#9770) with fakes: every target goes through
///     <see cref="Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext.OpenTenantAsync" /> as a Child target, copies keep the
///     source RtId and carry the origin the node stamps (never one from the input), an entity that is not this sync's
///     copy is never written, a refused target does not stop the others and the node fails afterwards naming it, and
///     without the setting nothing changes except the sync-origin write rules.
/// </summary>
public class ApplyChangesSyncNodeTests : SessionNodeTestBase
{
    private const string EntityUpdatesPath = "$.entityUpdates";
    private const string SourceTenant = "group";
    private static readonly RtCkId<CkTypeId> ContactType = new("TestModel/Contact");

    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Target
    {
        public Target(string tenantId)
        {
            TenantId = tenantId;
            A.CallTo(() => Access.TenantId).Returns(tenantId);
            A.CallTo(() => Access.Repository).Returns(Repository);
            A.CallTo(() => Access.OpenSessionAsync()).ReturnsLazily(() => Task.FromResult(Session));
            A.CallTo(() => Access.OpenSyncWriterSessionAsync(A<string>._))
                .ReturnsLazily(() => Task.FromResult(Session));
            A.CallTo(() => Repository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                    A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> _, IReadOnlyList<OctoObjectId> ids,
                        RtEntityQueryOptions _, int? _, int? _) =>
                    Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>(
                        Stored.Where(e => ids.Contains(e.RtId)).ToList(), Stored.Count, null, null)));
        }

        public string TenantId { get; }
        public ICrossTenantAccess Access { get; } = A.Fake<ICrossTenantAccess>();
        public ITenantRepository Repository { get; } = A.Fake<ITenantRepository>();
        public IOctoSession Session { get; } = A.Fake<IOctoSession>();
        public List<RtEntity> Stored { get; } = [];

        public IReadOnlyList<IEntityUpdateInfo<RtEntity>>? Written
        {
            get
            {
                var call = Fake.GetCalls(Repository).SingleOrDefault(c => c.Method.Name == "ApplyChangesAsync");
                return call?.Arguments[1] as IReadOnlyList<IEntityUpdateInfo<RtEntity>>;
            }
        }
    }

    public ApplyChangesSyncNodeTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(SourceTenant);
        AddTarget("companya");
        AddTarget("companyb");
        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._))
            .ReturnsLazily(() => Task.FromResult<IReadOnlyList<string>>(_targets.Keys.ToList()));
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .ReturnsLazily((TenantTargetConfiguration t, NodeExecutionIdentity _, IDataContext _, INodeContext _) =>
                _refused.Contains(t.TenantId!) || !_targets.TryGetValue(t.TenantId!, out var target)
                    ? Task.FromException<ICrossTenantAccess>(CrossTenantAccessDeniedException.Forbidden(
                        "transformations/0", "Child", CrossTenantRefusalReason.IdentityNotProvable))
                    : Task.FromResult(target.Access));
    }

    private Target AddTarget(string tenantId) => _targets[tenantId] = new Target(tenantId);

    private static ApplyChangesNodeConfiguration2 Sync(TenantSetConfiguration? set = null,
        NodeExecutionIdentity identity = NodeExecutionIdentity.ServiceAccount) => new()
    {
        EntityUpdatesPath = EntityUpdatesPath,
        Identity = identity,
        TargetTenants = set ?? new TenantSetConfiguration { Relation = TenantRelation.Child }
    };

    private ApplyChangesNode2 CreateNode(NodeDelegate next) =>
        new(next, EtlContext, ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) Prepare(
        ApplyChangesNodeConfiguration2 config, List<EntityUpdateInfo<RtEntity>> updates,
        IPipelineExecutionMode? executionMode = null)
    {
        var prepared = PrepareTest(config, executionMode: executionMode);
        A.CallTo(() => prepared.DataContext.Get<List<EntityUpdateInfo<RtEntity>>>(EntityUpdatesPath))
            .Returns(updates);
        return prepared;
    }

    private static RtEntity Contact(OctoObjectId rtId, string name, RtRecord? origin = null)
    {
        var attributes = new Dictionary<string, object?> { ["ContactName"] = name };
        if (origin != null)
        {
            attributes[SyncOrigin.AttributeName] = origin;
        }

        return new RtEntity(ContactType, rtId, attributes);
    }

    private static EntityUpdateInfo<RtEntity> Upsert(RtEntity entity) =>
        EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(ContactType, entity.RtId), entity);

    private static EntityUpdateInfo<RtEntity> Delete(OctoObjectId rtId) =>
        EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ContactType, rtId));

    private static RtSyncOriginRecord OriginOf(IEntityUpdateInfo<RtEntity> update) =>
        new((RtRecord)update.RtEntity!.Attributes[SyncOrigin.AttributeName]!);

    private Task RunAsync(ApplyChangesNodeConfiguration2 config, params EntityUpdateInfo<RtEntity>[] updates)
    {
        var (dataContext, nodeContext, next) = Prepare(config, updates.ToList());
        return CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);
    }

    // ---- fan-out --------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryChild_IsOpenedAsAChildTarget_AndGetsAnInsertWithTheSourceRtIdAndTheStampedOrigin()
    {
        var rtId = OctoObjectId.GenerateNewId();

        await RunAsync(Sync(), Upsert(Contact(rtId, "Anna")));

        foreach (var target in _targets.Values)
        {
            A.CallTo(() => EtlContext.OpenTenantAsync(
                    A<TenantTargetConfiguration>.That.Matches(t =>
                        t.Relation == TenantRelation.Child && t.TenantId == target.TenantId && t.TenantIdPath == null),
                    NodeExecutionIdentity.ServiceAccount, A<IDataContext>._, A<INodeContext>._))
                .MustHaveHappenedOnceExactly();
            var written = Assert.Single(target.Written!);
            Assert.Equal(EntityModOptions.Insert, written.ModOption);
            Assert.Equal(rtId, written.RtEntity!.RtId);
            Assert.Equal("Anna", written.RtEntity.Attributes["ContactName"]);
            Assert.Equal(SourceTenant, OriginOf(written).SourceTenantId);
            Assert.Equal(rtId.ToString(), OriginOf(written).SourceRtId);
            A.CallTo(() => target.Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        }

        // Never the own tenant, never the node's own session.
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AnOriginInTheInput_IsReplacedByTheOriginOfThisSync()
    {
        // Abuse case: the source entity names another tenant as its origin (or the pipeline forges one).
        var rtId = OctoObjectId.GenerateNewId();
        var forged = SyncOrigin.Create("othergroup", OctoObjectId.GenerateNewId(), DateTime.UtcNow);

        await RunAsync(Sync(), Upsert(Contact(rtId, "Anna", forged)));

        var written = Assert.Single(_targets["companya"].Written!);
        Assert.Equal(SourceTenant, OriginOf(written).SourceTenantId);
        Assert.Equal(rtId.ToString(), OriginOf(written).SourceRtId);
    }

    [Fact]
    public async Task ASelectedList_OpensExactlyTheListedTenants()
    {
        await RunAsync(Sync(new TenantSetConfiguration { Relation = TenantRelation.Child, TenantIds = [" companyb "] }),
            Upsert(Contact(OctoObjectId.GenerateNewId(), "Anna")));

        Assert.Null(_targets["companya"].Written);
        Assert.Single(_targets["companyb"].Written!);
        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task NoChildTenant_WritesNothing_AndContinues()
    {
        _targets.Clear();
        var (dataContext, nodeContext, next) = Prepare(Sync(), [Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    // ---- idempotency and ownership ---------------------------------------------------------------------------

    [Fact]
    public async Task AnExistingCopyOfThisSync_IsUpdated_NotInsertedAgain()
    {
        var rtId = OctoObjectId.GenerateNewId();
        foreach (var target in _targets.Values)
        {
            target.Stored.Add(Contact(rtId, "Old", SyncOrigin.Create(SourceTenant, rtId, DateTime.UtcNow)));
        }

        await RunAsync(Sync(), Upsert(Contact(rtId, "New")));

        foreach (var target in _targets.Values)
        {
            var written = Assert.Single(target.Written!);
            Assert.Equal(EntityModOptions.Update, written.ModOption);
            Assert.Equal(rtId, written.RtId);
            Assert.Equal("New", written.RtEntity!.Attributes["ContactName"]);
        }
    }

    [Fact]
    public async Task AReplaceOfTheSource_ReplacesTheExistingCopy_KeepingTheOrigin()
    {
        // The source dropped the attribute Phone with a replace: the copy must lose it too, so the write is a replace
        // of the whole copy (an update would keep Phone in the copy).
        var rtId = OctoObjectId.GenerateNewId();
        var stored = Contact(rtId, "Old", SyncOrigin.Create(SourceTenant, rtId, DateTime.UtcNow));
        stored.SetAttributeRawValue("Phone", "123");
        _targets["companya"].Stored.Add(stored);

        await RunAsync(Sync(), EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(ContactType, rtId),
            Contact(rtId, "New")));

        var replaced = Assert.Single(_targets["companya"].Written!);
        Assert.Equal(EntityModOptions.Replace, replaced.ModOption);
        Assert.Equal(rtId, replaced.RtId);
        Assert.Equal("New", replaced.RtEntity!.Attributes["ContactName"]);
        Assert.False(replaced.RtEntity.Attributes.ContainsKey("Phone"));
        Assert.Equal(SourceTenant, OriginOf(replaced).SourceTenantId);
        Assert.Equal(rtId.ToString(), OriginOf(replaced).SourceRtId);
        // Without a copy the replace inserts one.
        Assert.Equal(EntityModOptions.Insert, Assert.Single(_targets["companyb"].Written!).ModOption);
    }

    [Fact]
    public async Task TwoUpdatesOfTheSameEntity_InOneBatch_AreBothApplied_InOrder()
    {
        var rtId = OctoObjectId.GenerateNewId();
        _targets["companya"].Stored.Add(Contact(rtId, "Old", SyncOrigin.Create(SourceTenant, rtId, DateTime.UtcNow)));
        var first = Contact(rtId, "First");
        first.SetAttributeRawValue("Phone", "123");
        var second = new RtEntity(ContactType, rtId, new Dictionary<string, object?> { ["ContactName"] = "Second" });

        await RunAsync(Sync(), Upsert(first), Upsert(second));

        var written = Assert.Single(_targets["companya"].Written!);
        Assert.Equal(EntityModOptions.Update, written.ModOption);
        // The second update wins where both set an attribute; the attribute only the first set is not lost.
        Assert.Equal("Second", written.RtEntity!.Attributes["ContactName"]);
        Assert.Equal("123", written.RtEntity.Attributes["Phone"]);
        Assert.Equal(SourceTenant, OriginOf(written).SourceTenantId);
    }

    [Fact]
    public void SyncItems_ApplyTheChangesOfOneEntityInOrder()
    {
        var rtId = OctoObjectId.GenerateNewId();
        var id = new RtEntityId(ContactType, rtId);
        RtEntity With(params (string Name, object? Value)[] attributes) =>
            new(ContactType, rtId, attributes.ToDictionary(a => a.Name, a => a.Value));

        // replace + update = a replace with both
        var item = Assert.Single(ApplyChangesNode2.SyncItem.From([
            EntityUpdateInfo<RtEntity>.CreateReplace(id, With(("A", 1))),
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, With(("B", 2)))
        ], "transformations/0"));
        Assert.True(item.IsReplace);
        Assert.Equal(1, item.Attributes!["A"]);
        Assert.Equal(2, item.Attributes["B"]);

        // update + replace = the replace alone
        item = Assert.Single(ApplyChangesNode2.SyncItem.From([
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, With(("A", 1))),
            EntityUpdateInfo<RtEntity>.CreateReplace(id, With(("B", 2)))
        ], "transformations/0"));
        Assert.True(item.IsReplace);
        Assert.False(item.Attributes!.ContainsKey("A"));

        // update + delete = the delete
        item = Assert.Single(ApplyChangesNode2.SyncItem.From([
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, With(("A", 1))),
            EntityUpdateInfo<RtEntity>.CreateDelete(id)
        ], "transformations/0"));
        Assert.True(item.IsDelete);
    }

    [Fact]
    public async Task DeleteOfACopy_DeletesIt_AndDeleteOfAMissingCopy_WritesNothing()
    {
        var rtId = OctoObjectId.GenerateNewId();
        _targets["companya"].Stored.Add(Contact(rtId, "Old", SyncOrigin.Create(SourceTenant, rtId, DateTime.UtcNow)));

        await RunAsync(Sync(), Delete(rtId));

        var deleted = Assert.Single(_targets["companya"].Written!);
        Assert.Equal(EntityModOptions.Delete, deleted.ModOption);
        Assert.Equal(rtId, deleted.RtId);
        Assert.Null(_targets["companyb"].Written);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("othergroup")]
    public async Task AnEntityThatIsNotThisSyncsCopy_IsNeverWritten_AndItsTargetFails(string? originTenant)
    {
        // A local entity (no origin) or another source's copy under the same RtId.
        var rtId = OctoObjectId.GenerateNewId();
        var origin = originTenant == null ? null : SyncOrigin.Create(originTenant, rtId, DateTime.UtcNow);
        _targets["companya"].Stored.Add(Contact(rtId, "Local", origin));

        foreach (var update in new[] { Upsert(Contact(rtId, "New")), Delete(rtId) })
        {
            var failed = await Assert.ThrowsAsync<CrossTenantWriteFailedException>(() => RunAsync(Sync(), update));

            var failure = Assert.Single(failed.Failures);
            Assert.Equal("companya", failure.TenantId);
            Assert.Equal(SyncOriginCodes.SyncOwnedEntity, failure.ErrorCode);
        }

        Assert.Null(_targets["companya"].Written);
        A.CallTo(() => _targets["companya"].Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    // ---- failure semantics ------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedTarget_DoesNotStopTheOthers_AndTheNodeFailsAfterwardsNamingIt()
    {
        _refused.Add("companya");
        var (dataContext, nodeContext, next) = Prepare(Sync(), [Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))]);

        var failed = await Assert.ThrowsAsync<CrossTenantWriteFailedException>(() =>
            CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantWriteCodes.WriteFailed, failed.ErrorCode);
        Assert.Equal("companya", Assert.Single(failed.Failures).TenantId);
        Assert.Equal(CrossTenantAccessCodes.Forbidden, failed.Failures[0].ErrorCode);
        Assert.Equal(new[] { "companyb" }, failed.WrittenTenantIds);
        Assert.Contains("'companya' (CROSS_TENANT_FORBIDDEN)", failed.Message);
        Assert.Single(_targets["companyb"].Written!);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AFailingWrite_InOneTarget_IsNamed_AndTheOtherTargetIsWritten()
    {
        A.CallTo(() => _targets["companyb"].Repository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var failed = await Assert.ThrowsAsync<CrossTenantWriteFailedException>(() =>
            RunAsync(Sync(), Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))));

        Assert.Equal("companyb", Assert.Single(failed.Failures).TenantId);
        Assert.Equal("WRITE_FAILED", failed.Failures[0].ErrorCode);
        Assert.DoesNotContain("database down", failed.Message);
        A.CallTo(() => _targets["companya"].Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _targets["companyb"].Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task InADryRun_NothingIsWritten_AndTheIntentIsRecorded()
    {
        var (dataContext, nodeContext, next) = Prepare(Sync(),
            [Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))], new DefaultPipelineExecutionMode { IsDryRun = true });

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.All(_targets.Values, t => Assert.Null(t.Written));
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task InADryRun_WithOneFailedTarget_TheIntentKeepsTheOtherTargetsPlan_AndNamesOnlyTenantAndCode()
    {
        // The refusal is thrown for real (a stack trace and a TargetSite), as the guard throws it: an exception in
        // the intent made the serialisation fail, and the whole intent was recorded as null.
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>.That.Matches(t => t.TenantId == "companya"),
                A<NodeExecutionIdentity>._, A<IDataContext>._, A<INodeContext>._))
            .Throws(() => CrossTenantAccessDeniedException.Forbidden("transformations/0", "Child",
                CrossTenantRefusalReason.IdentityNotProvable));
        var debugger = A.Fake<IPipelineDebugger>();
        var config = Sync();
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), "ApplyChanges", config, debugger,
            new DefaultPipelineExecutionMode { IsDryRun = true });
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(EntityUpdatesPath))
            .Returns([Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))]);

        var failed = await Assert.ThrowsAsync<CrossTenantWriteFailedException>(() =>
            CreateNode(A.Fake<NodeDelegate>()).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("companya", Assert.Single(failed.Failures).TenantId);
        Assert.All(_targets.Values, t => Assert.Null(t.Written));
        var call = Assert.Single(Fake.GetCalls(debugger), c => c.Method.Name == nameof(IPipelineDebugger.RecordDryRunIntent));
        Assert.Equal(DryRunHonouredLoadNodes.ApplyChanges2, call.Arguments[4]);
        var intent = Assert.IsAssignableFrom<JsonNode>(call.Arguments[5]);

        var plan = Assert.Single(intent["syncTargets"]!.AsArray())!;
        Assert.Equal("companyb", plan["targetTenantId"]!.GetValue<string>());
        Assert.Equal(1, plan["inserts"]!.GetValue<int>());
        Assert.Single(plan["wouldApplyEntityUpdates"]!.AsArray());

        var failedTarget = Assert.Single(intent["failedTargets"]!.AsArray())!.AsObject();
        Assert.Equal(["errorCode", "tenantId"], failedTarget.Select(p => p.Key).Order());
        Assert.Equal("companya", failedTarget["tenantId"]!.GetValue<string>());
        Assert.Equal(CrossTenantAccessCodes.Forbidden, failedTarget["errorCode"]!.GetValue<string>());
    }

    // ---- configuration --------------------------------------------------------------------------------------

    public static TheoryData<string, ApplyChangesNodeConfiguration2> InvalidConfigurations => new()
    {
        {
            "not both", Sync() with
            {
                TargetTenant = new TenantTargetConfiguration { Relation = TenantRelation.Child, TenantId = "companya" }
            }
        },
        { "Sibling", Sync(new TenantSetConfiguration { Relation = TenantRelation.Sibling }) },
        { "Parent", Sync(new TenantSetConfiguration { Relation = TenantRelation.Parent }) },
        { "needs a relation", Sync(new TenantSetConfiguration { Relation = null }) },
        { "empty", Sync(new TenantSetConfiguration { Relation = TenantRelation.Child, TenantIds = [] }) },
        { "association", Sync() with { AssociationUpdatesPath = "$.associations" } },
        { "Report", Sync() with { OnDuplicateKey = DuplicateKeyHandling.Report, DuplicateKeyTargetPath = "$.dup" } }
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public async Task AnInvalidSyncConfiguration_IsRefused_BeforeAnyTargetIsOpened(string expected,
        ApplyChangesNodeConfiguration2 config)
    {
        var refused = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            RunAsync(config, Upsert(Contact(OctoObjectId.GenerateNewId(), "A"))));

        Assert.Contains(expected, refused.Message);
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AnUpdateWithoutTheSourceRtId_IsRefused()
    {
        var withoutId = EntityUpdateInfo<RtEntity>.CreateInsert(new RtEntity(ContactType, OctoObjectId.Empty));

        await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() => RunAsync(Sync(), withoutId));
    }

    // ---- sync-origin write rules: the target's engine enforces them (SyncOriginWriteRules); integration tests

    [Fact]
    public async Task TheSync_WritesEveryTarget_WithASyncWriterSessionOfThePipelinesTenant()
    {
        await RunAsync(Sync(), Upsert(Contact(OctoObjectId.GenerateNewId(), "Contact")));

        foreach (var target in _targets.Values)
        {
            A.CallTo(() => target.Access.OpenSyncWriterSessionAsync(SourceTenant)).MustHaveHappenedOnceExactly();
            A.CallTo(() => target.Access.OpenSessionAsync()).MustNotHaveHappened();
        }
    }


    [Fact]
    public async Task OwnTenantUpdate_OfAnEntityWithoutOrigin_IsWrittenAsBefore()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };

        await RunAsync(config, Upsert(Contact(OctoObjectId.GenerateNewId(), "Local")));

        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
    }
}

/// <summary>AB#9770: the pure parts of the sync-origin rules and the registry's child list.</summary>
public class SyncOriginRulesTests
{
    private static readonly RtCkId<CkTypeId> ContactType = new("TestModel/Contact");

    private static RtEntity WithAttribute(string name, object? value) =>
        new(ContactType, OctoObjectId.GenerateNewId(), new Dictionary<string, object?> { [name] = value });

    [Fact]
    public void IsCopyOf_NeedsTheSameSourceTenantAndRtId()
    {
        var rtId = OctoObjectId.GenerateNewId();
        Assert.True(SyncOrigin.IsCopyOf(("Group ", rtId.ToString()), "group", rtId));
        Assert.False(SyncOrigin.IsCopyOf(("other", rtId.ToString()), "group", rtId));
        Assert.False(SyncOrigin.IsCopyOf(("group", OctoObjectId.GenerateNewId().ToString()), "group", rtId));
        Assert.False(SyncOrigin.IsCopyOf((null, null), "group", rtId));
        Assert.False(SyncOrigin.IsCopyOf(null, "group", rtId));
    }

    [Fact]
    public void AnOriginThatIsNoRecord_StillCountsAsAnOrigin()
    {
        Assert.NotNull(SyncOrigin.Read(WithAttribute("RtSyncOrigin", "forged text")));
        Assert.Null(SyncOrigin.Read(WithAttribute("ContactName", "x")));
    }

    [Fact]
    public void ChildrenOf_ListsOnlyRegisteredDirectChildren()
    {
        var registry = new TenantRegistrySnapshot("octosystem",
        [
            ("group", null), ("companya", "group"), ("CompanyB", "Group"), ("grandchild", "companya"),
            ("othergroup", null), ("outsider", "othergroup")
        ]);

        Assert.Equal(new[] { "companya", "CompanyB" }, registry.ChildrenOf("group"));
        Assert.Equal(new[] { "grandchild" }, registry.ChildrenOf("companya"));
        Assert.Empty(registry.ChildrenOf("octosystem"));
        Assert.Empty(registry.ChildrenOf("unknown"));
        Assert.Empty(registry.ChildrenOf("outsider"));
    }
}

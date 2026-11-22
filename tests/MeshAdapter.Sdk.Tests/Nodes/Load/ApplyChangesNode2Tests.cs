using System.Net;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class ApplyChangesNode2Tests : SessionNodeTestBase
{
    private const string EntityUpdatesPath = "$.entityUpdates";
    private const string AssociationUpdatesPath = "$.associationUpdates";
    private const string DuplicateTargetPath = "$.duplicate";


    public ApplyChangesNode2Tests()
    {

    }

    private ApplyChangesNode2 CreateNode(NodeDelegate next)
    {
        return new ApplyChangesNode2(next, EtlContext, MeshAdapter.Sdk.Tests.Helpers.ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());
    }

    private static RtEntity CreateRtEntity(string? rtId = null)
    {
        var ckTypeId = new RtCkId<CkTypeId>("TestModel/TestType");
        var id = new OctoObjectId(rtId ?? "000000000000000000000001");
        return new RtEntity(ckTypeId, id);
    }

    private static EntityUpdateInfo<RtEntity> CreateInsertUpdateInfo(string? rtId = null)
    {
        var entity = CreateRtEntity(rtId);
        return EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("TestModel/TestType"), entity);
    }

    private static EntityUpdateInfo<RtEntity> CreateUpdateUpdateInfo(string? rtId = null)
    {
        var entity = CreateRtEntity(rtId ?? "000000000000000000000001");
        var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>("TestModel/TestType"), entity.RtId);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, entity);
    }

    private static RtEntityId CreateRtEntityId(string? rtId = null)
    {
        return new RtEntityId(
            new RtCkId<CkTypeId>("TestModel/TestType"),
            new OctoObjectId(rtId ?? "000000000000000000000001"));
    }

    private static AssociationUpdateInfo CreateAssociationInsert(string originRtId, string targetRtId)
    {
        return AssociationUpdateInfo.CreateInsert(
            CreateRtEntityId(originRtId),
            CreateRtEntityId(targetRtId),
            new RtCkId<CkAssociationRoleId>("TestModel/TestRole"));
    }

    private static AssociationUpdateInfo CreateAssociationDelete(string originRtId, string targetRtId)
    {
        return AssociationUpdateInfo.CreateDelete(
            CreateRtEntityId(originRtId),
            CreateRtEntityId(targetRtId),
            new RtCkId<CkAssociationRoleId>("TestModel/TestRole"));
    }

    private static void SetupEntityData(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(path))
            .Returns(data);
    }

    private static void SetupAssociationData(IDataContext dataContext, string path,
        List<AssociationUpdateInfo>? data)
    {
        A.CallTo(() => dataContext.Get<List<AssociationUpdateInfo>>(path))
            .Returns(data);
    }

    /// <summary>
    ///     The caller-scoped write AB#4975 introduced, finally under test (AB#5028): before the
    ///     strict fake, <c>ITenantRepository</c> did not implement <see cref="ISecureSessionFactory" />
    ///     and the extension fell back to a system session in silence, so this branch was green
    ///     without ever having enforced anything.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_OpensTheSessionUnderTheExecutionIdentity()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        SetupEntityData(dataContext, EntityUpdatesPath, [CreateInsertUpdateInfo()]);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEntityUpdates_CommitsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAssociationUpdates_CommitsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2 { AssociationUpdatesPath = AssociationUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var assocData = new List<AssociationUpdateInfo>
        {
            CreateAssociationInsert("000000000000000000000001", "000000000000000000000002")
        };
        SetupAssociationData(dataContext, AssociationUpdatesPath, assocData);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithBothEntityAndAssociationUpdates_AppliesAll()
    {
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            AssociationUpdatesPath = AssociationUpdatesPath
        };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var entityData = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, entityData);

        var assocData = new List<AssociationUpdateInfo>
        {
            CreateAssociationInsert("000000000000000000000001", "000000000000000000000002")
        };
        SetupAssociationData(dataContext, AssociationUpdatesPath, assocData);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateEntityUpdates_DeduplicatesByRtEntityId()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateUpdateUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000001")
        };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedEntities = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> entities,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) =>
                capturedEntities = entities);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedEntities);
        Assert.Single(capturedEntities!);
    }

    // ---- AB#7436: structural comparison of attribute values when several updates hit one entity ----

    private static RtRecord StateRecord(string uuid, object? value)
    {
        return new RtRecord(new RtCkId<CkRecordId>("TestModel/State"),
            new Dictionary<string, object?> { ["Uuid"] = uuid, ["CurrentValue"] = value });
    }

    private static EntityUpdateInfo<RtEntity> UpdateWith(params (string Name, object? Value)[] attributes)
    {
        var entity = CreateRtEntity("000000000000000000000001");
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        return EntityUpdateInfo<RtEntity>.CreateUpdate(CreateRtEntityId("000000000000000000000001"), entity);
    }

    private async Task<(RtEntity Merged, IPipelineLogger Logger)> MergeAsync(params EntityUpdateInfo<RtEntity>[] updates)
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath, [.. updates]);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => captured = e);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        return (Assert.Single(captured!).RtEntity!, logger);
    }

    private static string[] WarningTexts(IPipelineLogger logger)
    {
        return Fake.GetCalls(logger).Where(c => c.Method.Name == nameof(IPipelineLogger.Warning))
            .Select(c => string.Join(" | ", new[] { (string)c.Arguments[2]! }.Concat(
                ((object[])c.Arguments[3]!).Select(a => a?.ToString() ?? ""))))
            .ToArray();
    }

    [Fact]
    public async Task Merge_EqualListsOfRecords_LogsNoWarning()
    {
        var (merged, logger) = await MergeAsync(
            UpdateWith(("States", new List<object?> { StateRecord("a", 1), StateRecord("b", "x") })),
            UpdateWith(("States", new List<object?> { StateRecord("a", 1), StateRecord("b", "x") })));

        Assert.Empty(WarningTexts(logger));
        Assert.Equal(2, ((List<object?>)merged.Attributes["States"]!).Count);
    }

    [Fact]
    public async Task Merge_IntVersusLongVersusDouble_OfEqualValue_LogsNoWarning()
    {
        var (_, logger) = await MergeAsync(
            UpdateWith(("Count", 5), ("Items", new List<object?> { 1, 2.0 })),
            UpdateWith(("Count", 5L), ("Items", new List<object?> { 1L, 2 })),
            UpdateWith(("Count", 5.0)));

        Assert.Empty(WarningTexts(logger));
    }

    [Fact]
    public async Task Merge_EqualNestedDictionariesAndNulls_LogsNoWarning()
    {
        var (_, logger) = await MergeAsync(
            UpdateWith(("Cfg", new Dictionary<string, object?>
            { ["a"] = new List<object?> { new Dictionary<string, object?> { ["x"] = null } }, ["b"] = "s" }),
                ("Nothing", null)),
            UpdateWith(("Cfg", new Dictionary<string, object?>
            { ["b"] = "s", ["a"] = new List<object?> { new Dictionary<string, object?> { ["x"] = null } } }),
                ("Nothing", null)));

        Assert.Empty(WarningTexts(logger));
    }

    [Fact]
    public async Task Merge_EqualJsonElementAndClrValues_LogsNoWarning()
    {
        using var doc = JsonDocument.Parse("""{"n":3,"l":[1,"a"]}""");
        var (_, logger) = await MergeAsync(
            UpdateWith(("Cfg", doc.RootElement.Clone())),
            UpdateWith(("Cfg", new Dictionary<string, object?> { ["n"] = 3L, ["l"] = new List<object?> { 1, "a" } })));

        Assert.Empty(WarningTexts(logger));
    }

    [Fact]
    public async Task Merge_DifferentLists_LogsExactlyOneWarningWithReadableValues_AndLaterWins()
    {
        var first = new List<object?> { StateRecord("a", 1), StateRecord("b", 10) };
        var second = new List<object?> { StateRecord("a", 1), StateRecord("b", 20) };
        var (merged, logger) = await MergeAsync(
            UpdateWith(("States", first), ("LastStateUpdate", new DateTime(2026, 10, 24, 11, 43, 23, DateTimeKind.Utc))),
            UpdateWith(("States", second), ("LastStateUpdate", new DateTime(2026, 10, 24, 11, 43, 24, DateTimeKind.Utc))));

        var warning = Assert.Single(WarningTexts(logger));
        Assert.Contains("attributes", warning);
        Assert.Contains("'States'", warning);
        Assert.Contains("'LastStateUpdate'", warning);
        Assert.Contains("\"CurrentValue\":10", warning);
        Assert.Contains("\"CurrentValue\":20", warning);
        Assert.DoesNotContain("System.Collections.Generic", warning);
        Assert.Same(second, merged.Attributes["States"]);
    }

    [Fact]
    public async Task Merge_ThreeUpdatesConflictingTwice_StillOneWarningPerEntity()
    {
        var (merged, logger) = await MergeAsync(
            UpdateWith(("Position", 1)), UpdateWith(("Position", 2)), UpdateWith(("Position", 3)));

        Assert.Single(WarningTexts(logger));
        Assert.Equal(3, merged.Attributes["Position"]);
    }

    // ---- AB#7442: atomic RecordArray item updates travel through the node unchanged ----

    private static RtRecordArrayItemUpdate ItemUpdate(string key, object value) =>
        new("States", "ExternalId", key, new Dictionary<string, object?> { ["CurrentValue"] = value });

    private static EntityUpdateInfo<RtEntity> ItemUpdateInfo(params RtRecordArrayItemUpdate[] items)
    {
        return EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(CreateRtEntityId("000000000000000000000001"),
            CreateRtEntity("000000000000000000000001"), items);
    }

    [Fact]
    public async Task ItemUpdates_OfSeveralInfosForOneEntity_AreMergedInOrder_AndStayItemUpdates()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath,
            [ItemUpdateInfo(ItemUpdate("a", 1)), ItemUpdateInfo(ItemUpdate("b", 2), ItemUpdate("a", 3))]);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => captured = e);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var merged = Assert.Single(captured!);
        Assert.Equal(["a", "b", "a"], merged.RecordArrayItemUpdates!.Select(i => (string)i.MatchValue));
        Assert.Equal([1, 2, 3], merged.RecordArrayItemUpdates!.Select(i => (int)i.MemberUpdates["CurrentValue"]!));
        Assert.Empty(WarningTexts(logger));
    }

    [Fact]
    public async Task ItemUpdate_ThatMatchedNoItem_IsAWarning_AndTheTransactionIsCommitted()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath, [ItemUpdateInfo(ItemUpdate("missing", 1))]);
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult result) =>
                result.AddMessage(new OperationMessage(MessageLevel.Warning, null, 27,
                    "No item of the record array(s) 'States' matched")));

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var warning = Assert.Single(WarningTexts(logger));
        Assert.Contains("matched", warning);
        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.AbortTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task Merge_VeryLongValues_AreTruncated()
    {
        var (_, logger) = await MergeAsync(
            UpdateWith(("Text", new string('a', 5000))), UpdateWith(("Text", new string('b', 5000))));

        var warning = Assert.Single(WarningTexts(logger));
        Assert.True(warning.Length < 700, $"warning has {warning.Length} chars");
        Assert.Contains("…", warning);
    }

    [Fact]
    public async Task Merge_ConflictingCredentialAttribute_DoesNotPrintTheValues()
    {
        var (_, logger) = await MergeAsync(
            UpdateWith(("ApiPassword", "first-plain")), UpdateWith(("ApiPassword", "second-plain")));

        var warning = Assert.Single(WarningTexts(logger));
        Assert.Contains("'ApiPassword'", warning);
        Assert.DoesNotContain("first-plain", warning);
        Assert.DoesNotContain("second-plain", warning);
    }

    [Theory]
    [InlineData(1, "1", false)]
    [InlineData(1, 1, true)]
    [InlineData(null, 0, false)]
    [InlineData("a", "a", true)]
    public void AttributeValueComparer_Scalars(object? left, object? right, bool expected)
    {
        Assert.Equal(expected, AttributeValueComparer.AreEqual(left, right));
    }

    [Fact]
    public void AttributeValueComparer_ListsOfDifferentLength_AreNotEqual()
    {
        Assert.False(AttributeValueComparer.AreEqual(new List<object?> { 1 }, new List<object?> { 1, 2 }));
        Assert.False(AttributeValueComparer.AreEqual(StateRecord("a", 1), StateRecord("a", 2)));
        Assert.True(AttributeValueComparer.AreEqual(StateRecord("a", 1), StateRecord("a", 1L)));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateAssociationUpdates_DeduplicatesByOriginAndTarget()
    {
        var config = new ApplyChangesNodeConfiguration2 { AssociationUpdatesPath = AssociationUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var assocData = new List<AssociationUpdateInfo>
        {
            CreateAssociationDelete("000000000000000000000001", "000000000000000000000002"),
            CreateAssociationDelete("000000000000000000000001", "000000000000000000000002")
        };
        SetupAssociationData(dataContext, AssociationUpdatesPath, assocData);

        IReadOnlyList<AssociationUpdateInfo>? capturedAssociations = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                IReadOnlyList<AssociationUpdateInfo> assocs, OperationResult _) =>
                capturedAssociations = assocs);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedAssociations);
        Assert.Single(capturedAssociations!);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullPaths_DoesNotStartTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2();
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertNoSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotStartTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            AssociationUpdatesPath = AssociationUpdatesPath
        };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>>());
        SetupAssociationData(dataContext, AssociationUpdatesPath, new List<AssociationUpdateInfo>());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertNoSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration2();
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMixedInsertAndUpdateEntities_KeepsAllInsertsAndDedupsUpdates()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000002"),
            CreateUpdateUpdateInfo("000000000000000000000002")
        };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedEntities = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> entities,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) =>
                capturedEntities = entities);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedEntities);
        Assert.Equal(2, capturedEntities!.Count);
    }

    /// <summary>
    /// The duplicate-key behaviour is opt-in. A caller that never asked to hear about a unique
    /// index must keep failing rather than silently storing nothing - the node has always thrown,
    /// and every existing write pipeline relies on that.
    /// </summary>
    [Fact]
    public void Duplicate_key_reporting_is_off_unless_the_pipeline_asks_for_it()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };

        Assert.Equal(DuplicateKeyHandling.Throw, config.OnDuplicateKey);
        Assert.Null(config.DuplicateKeyTargetPath);
    }



    /// <summary>
    /// Report without somewhere to put the flag is not reporting. The decision lives in the
    /// exception filter, so when it says no the duplicate keeps travelling and the node throws -
    /// the alternative is a rolled-back write that every later node reads as a success.
    /// </summary>
    [Theory]
    [InlineData(DuplicateKeyHandling.Report, "$.duplicate", true)]
    [InlineData(DuplicateKeyHandling.Report, null, false)]
    [InlineData(DuplicateKeyHandling.Report, "", false)]
    [InlineData(DuplicateKeyHandling.Report, "   ", false)]
    [InlineData(DuplicateKeyHandling.Throw, "$.duplicate", false)]
    public void A_duplicate_key_is_reported_only_with_a_usable_target(
        DuplicateKeyHandling handling, string? targetPath, bool expected)
    {
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            OnDuplicateKey = handling,
            DuplicateKeyTargetPath = targetPath
        };

        Assert.Equal(expected, ApplyChangesNode2.CanReportDuplicateKey(config));
    }

    /// <summary>
    /// The warning names the index and never the value that collided. MongoDB puts the duplicate
    /// key itself in the message, and on the public registration route that key is an applicant's
    /// e-mail address.
    /// </summary>
    [Theory]
    [InlineData(
        "E11000 duplicate key error collection: tenant.RtEntity_Registration index: Registration_0 dup key: { attributes.eMail: \"someone@example.at\" }",
        "index Registration_0")]
    [InlineData("a shape this parser has never seen", "a unique index")]
    [InlineData("", "a unique index")]
    [InlineData(null, "a unique index")]
    public void The_duplicate_key_warning_names_the_index_and_nothing_else(
        string? message, string expected)
    {
        var described = ApplyChangesNode2.IndexNameOf(message);

        Assert.Equal(expected, described);
        Assert.DoesNotContain("someone@example.at", described);
    }

    /// <summary>
    /// Builds the exception the driver raises when a unique index refuses a write. BulkWriteError
    /// has no public constructor, so it is built through the internal one - the alternative is a
    /// test that cannot reach the branch it is written for. Everything else on the way
    /// (MongoBulkWriteException, BulkWriteResult.Acknowledged, ConnectionId) is public.
    /// </summary>
    private static MongoBulkWriteException<BsonDocument> DuplicateKeyFailure(
        string message = "E11000 duplicate key error collection: t.RtEntity_X index: EMail_1 dup key: { eMail: \"a@b.at\" }")
    {
        var errorCtor = typeof(BulkWriteError).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var error = (BulkWriteError)errorCtor.Invoke(
            [0, ServerErrorCategory.DuplicateKey, 11000, message, new BsonDocument()]);

        var processed = new List<WriteModel<BsonDocument>>();
        var result = new BulkWriteResult<BsonDocument>.Acknowledged(
            1, 0, 0, 0, 0, processed, []);
        var connection = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));

        return new MongoBulkWriteException<BsonDocument>(
            connection, result, [error], null, processed);
    }

    /// <summary>
    /// The repository wraps the driver failure before it reaches the node, and IsDuplicateKey walks
    /// the InnerException chain because of it. The repository's own wrapper type is not visible from
    /// this assembly and is not what is under test - the chain-walking is - so any wrapper proves it.
    /// </summary>
    private static Exception Wrapped(Exception inner)
    {
        return new InvalidOperationException("Applying changes failed.", inner);
    }

    private void GivenApplyChangesThrows(Exception failure)
    {
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Throws(failure);
    }

    private static ApplyChangesNodeConfiguration2 ReportingConfig()
    {
        return new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            OnDuplicateKey = DuplicateKeyHandling.Report,
            DuplicateKeyTargetPath = DuplicateTargetPath
        };
    }

    /// <summary>
    /// The whole point of Report: the write is rolled back, the caller is told through the flag, and
    /// the pipeline carries on. Verified end to end against a live tenant in the consumer repo,
    /// which is real but lives in another repository and does not run in this one's CI.
    /// </summary>
    [Fact]
    public async Task A_refused_write_is_rolled_back_flagged_and_the_pipeline_continues()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(DuplicateKeyFailure()));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    /// <summary>
    /// The same failure unwrapped. The node has to recognise it wherever it sits in the chain, and
    /// a wrapper that changes shape must not turn a reported duplicate into a thrown one.
    /// </summary>
    [Fact]
    public async Task A_refused_write_is_recognised_without_a_wrapper()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(DuplicateKeyFailure());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A run that stored the record writes false, so a true left by an earlier iteration cannot
    /// answer "already on file" for a record this run just stored. The node runs more than once
    /// against one data document whenever a ForEach or a second apply step wraps it.
    /// </summary>
    [Fact]
    public async Task A_stored_record_leaves_the_flag_false_rather_than_absent()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, false, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Without Report the duplicate keeps travelling. A pipeline that never asked to hear about a
    /// unique index must still fail, or a rolled-back write reads downstream as a stored one.
    /// </summary>
    [Fact]
    public async Task Without_reporting_a_refused_write_still_throws()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(DuplicateKeyFailure()));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        A.CallTo(() => dataContext.Set(A<string>._, A<bool>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    // ----- AB#9466: write conflicts (112) across adapter replicas -----

    private static MongoCommandException WriteConflict()
    {
        var connection = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(connection, "WriteConflict error: this operation conflicted",
            new BsonDocument("insert", "RtEntity_SlotBooking"),
            new BsonDocument
            {
                { "ok", 0 }, { "code", 112 }, { "codeName", "WriteConflict" },
                { "errmsg", "WriteConflict error: this operation conflicted" }
            });
    }

    private static MongoCommandException OtherCommandFailure()
    {
        var connection = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(connection, "not authorized",
            new BsonDocument("insert", "RtEntity_SlotBooking"),
            new BsonDocument { { "ok", 0 }, { "code", 13 }, { "codeName", "Unauthorized" } });
    }

    private static MongoBulkWriteException<BsonDocument> BulkWriteConflict()
    {
        var errorCtor = typeof(BulkWriteError).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var error = (BulkWriteError)errorCtor.Invoke(
            [0, ServerErrorCategory.Uncategorized, 112, "WriteConflict", new BsonDocument()]);
        var processed = new List<WriteModel<BsonDocument>>();
        var result = new BulkWriteResult<BsonDocument>.Acknowledged(1, 0, 0, 0, 0, processed, []);
        var connection = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoBulkWriteException<BsonDocument>(connection, result, [error], null, processed);
    }

    private void GivenApplyChangesThrowsThenSucceeds(int failures, Exception failure)
    {
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Throws(failure).NumberOfTimes(failures)
            .Then.Returns(Task.CompletedTask);
    }

    private void VerifyApplyChangesCalled(int times)
    {
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappened(times, Times.Exactly);
    }

    [Fact]
    public void A_write_conflict_is_recognised_anywhere_in_the_exception_chain()
    {
        Assert.True(ApplyChangesNode2.IsWriteConflict(WriteConflict()));
        Assert.True(ApplyChangesNode2.IsWriteConflict(Wrapped(WriteConflict())));
        Assert.True(ApplyChangesNode2.IsWriteConflict(Wrapped(Wrapped(WriteConflict()))));
        Assert.True(ApplyChangesNode2.IsWriteConflict(Wrapped(BulkWriteConflict())));
    }

    [Fact]
    public void A_duplicate_key_or_another_failure_is_no_write_conflict()
    {
        Assert.False(ApplyChangesNode2.IsWriteConflict(null));
        Assert.False(ApplyChangesNode2.IsWriteConflict(Wrapped(DuplicateKeyFailure())));
        Assert.False(ApplyChangesNode2.IsWriteConflict(Wrapped(OtherCommandFailure())));
        Assert.False(ApplyChangesNode2.IsWriteConflict(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData(1, 12.5, 25)]
    [InlineData(2, 25, 50)]
    [InlineData(3, 50, 100)]
    [InlineData(4, 100, 200)]
    [InlineData(5, 200, 400)]
    [InlineData(9, 200, 400)]
    [InlineData(40, 200, 400)]
    public void The_backoff_is_short_jittered_and_capped(int attempt, double minMs, double maxMs)
    {
        var random = new Random(4711);
        var delays = Enumerable.Range(0, 200)
            .Select(_ => ApplyChangesNode2.WriteConflictBackoff(attempt, random).TotalMilliseconds)
            .ToList();

        Assert.All(delays, d => Assert.InRange(d, minMs, maxMs));
        Assert.True(delays.Distinct().Count() > 1, "the wait is jittered, so two replicas do not retry in lock step");
    }

    /// <summary>
    /// A wrapped WriteConflict used to escape the node and fail the pipeline. Now the transaction is aborted and the
    /// write retried on a fresh session; the retry stores the record.
    /// </summary>
    [Fact]
    public async Task A_wrapped_write_conflict_is_aborted_and_retried_and_the_retry_stores_the_record()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrowsThenSucceeds(2, Wrapped(WriteConflict()));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyApplyChangesCalled(3);
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedTwiceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, false, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustNotHaveHappened();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    /// <summary>
    /// The other replica commits first: the retry meets the unique index and is reported as a duplicate.
    /// </summary>
    [Fact]
    public async Task A_write_conflict_followed_by_a_duplicate_key_is_reported_as_a_duplicate()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Throws(Wrapped(WriteConflict())).Once()
            .Then.Throws(Wrapped(DuplicateKeyFailure()));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyApplyChangesCalled(2);
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    /// <summary>
    /// The conflict outlasts every attempt. With Report the node reports a conflict - the flag is true and nothing
    /// was committed - instead of ending as a silent success, which the pipeline would have read as "booked".
    /// </summary>
    [Fact]
    public async Task A_write_conflict_that_outlasts_the_retries_is_reported_never_a_silent_success()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(WriteConflict()));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyApplyChangesCalled(ApplyChangesNode2.MaxWriteAttempts);
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappened(ApplyChangesNode2.MaxWriteAttempts, Times.Exactly);
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, false, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustNotHaveHappened();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    /// <summary>
    /// The same unwrapped (how the node's old catch saw it): when the attempts ran out, the old loop ended without an
    /// error and without the flag.
    /// </summary>
    [Fact]
    public async Task An_unwrapped_write_conflict_that_outlasts_the_retries_is_reported_too()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(WriteConflict());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Without Report a conflict that outlasts the retries fails the node, as a duplicate key does.
    /// </summary>
    [Fact]
    public async Task Without_reporting_a_write_conflict_that_outlasts_the_retries_throws()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(WriteConflict()));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyApplyChangesCalled(ApplyChangesNode2.MaxWriteAttempts);
        A.CallTo(() => dataContext.Set(A<string>._, A<bool>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => next.Invoke(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// Report without a target path cannot report (CanReportDuplicateKey), so it throws like Throw.
    /// </summary>
    [Fact]
    public async Task Report_without_a_target_path_throws_when_the_conflict_outlasts_the_retries()
    {
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            OnDuplicateKey = DuplicateKeyHandling.Report
        };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(WriteConflict()));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>
    /// A failure that is no write conflict is not retried and travels as before, Report or not.
    /// </summary>
    [Fact]
    public async Task Another_command_failure_is_not_retried_and_still_throws()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrows(Wrapped(OtherCommandFailure()));

        var node = CreateNode(next);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyApplyChangesCalled(1);
        A.CallTo(() => dataContext.Set(A<string>._, A<bool>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// An abort that fails (the server already aborted the transaction) does not hide the conflict being handled.
    /// </summary>
    [Fact]
    public async Task A_failing_abort_does_not_hide_the_write_conflict()
    {
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataContext, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() });
        GivenApplyChangesThrowsThenSucceeds(1, Wrapped(WriteConflict()));
        A.CallTo(() => Session.AbortTransactionAsync()).Throws(new InvalidOperationException("no transaction"));

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(DuplicateTargetPath, false, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// The backoff waits without the process-wide lock: while one write sits out its conflict retries, another
    /// ApplyChanges@2 run of the same adapter goes through instead of queueing behind it (review of PR #130).
    /// </summary>
    [Fact]
    public async Task The_backoff_releases_the_process_wide_lock_so_other_writes_go_on()
    {
        const string conflicting = "0000000000000000000000a1";
        const string other = "0000000000000000000000b2";
        var (dataA, nodeContextA, nextA) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataA, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo(conflicting) });
        var (dataB, nodeContextB, nextB) = PrepareTest<ApplyChangesNodeConfiguration2>(ReportingConfig());
        SetupEntityData(dataB, EntityUpdatesPath, new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo(other) });
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>.That.Matches(
                    list => list.Any(u => u.RtEntity != null && u.RtEntity.RtId.ToString() == conflicting)),
                A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Throws(Wrapped(WriteConflict()));

        var conflictingRun = CreateNode(nextA).ProcessObjectAsync(dataA, nodeContextA);
        await Task.Delay(TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken);
        var otherRun = CreateNode(nextB).ProcessObjectAsync(dataB, nodeContextB);

        var first = await Task.WhenAny(conflictingRun, otherRun);
        Assert.Same(otherRun, first);
        await conflictingRun;
        A.CallTo(() => dataB.Set(DuplicateTargetPath, false, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataA.Set(DuplicateTargetPath, true, DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }
}

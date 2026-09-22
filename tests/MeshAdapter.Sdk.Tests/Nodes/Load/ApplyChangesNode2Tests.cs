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
        return new ApplyChangesNode2(next, EtlContext);
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
}

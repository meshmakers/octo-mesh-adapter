using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
///     The target-tenant setting of ApplyChanges@2 (AB#9769): with it, every create, update and delete goes through
///     <see cref="Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext.OpenTenantAsync" /> (the guard) and uses only the
///     repository and the session the guard returned; the own repository and the node identity's own session are never
///     used. A refusal fails the node before anything is written, also in a dry run. The batch is judged with the
///     target's CK model and without the run's method marker. Without the setting the guard is never asked.
/// </summary>
public class ApplyChangesCrossTenantNodeTests : SessionNodeTestBase
{
    private const string EntityUpdatesPath = "$.entityUpdates";
    private const string AssociationUpdatesPath = "$.associationUpdates";
    private const string TargetTenantId = "companyb";
    private static readonly RtCkId<CkTypeId> DocumentType = new("TestModel/IncomingDocument");

    private static readonly TenantTargetConfiguration Sibling =
        new() { Relation = TenantRelation.Sibling, TenantId = TargetTenantId };

    private readonly ITenantRepository _targetRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _targetSession = A.Fake<IOctoSession>();
    private readonly ICrossTenantAccess _access = A.Fake<ICrossTenantAccess>();

    public ApplyChangesCrossTenantNodeTests()
    {
        A.CallTo(() => _access.TenantId).Returns(TargetTenantId);
        A.CallTo(() => _access.Repository).Returns(_targetRepository);
        A.CallTo(() => _access.OpenSessionAsync()).Returns(Task.FromResult(_targetSession));
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .Returns(Task.FromResult(_access));
    }

    private ApplyChangesNode2 CreateNode(NodeDelegate next, ICkCacheService? cache = null) =>
        new(next, EtlContext, cache ?? ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) Prepare(
        ApplyChangesNodeConfiguration2 config, List<EntityUpdateInfo<RtEntity>> updates,
        IPipelineExecutionMode? executionMode = null)
    {
        var prepared = PrepareTest(config, executionMode: executionMode);
        A.CallTo(() => prepared.DataContext.Get<List<EntityUpdateInfo<RtEntity>>>(EntityUpdatesPath))
            .Returns(updates);
        return prepared;
    }

    private static List<EntityUpdateInfo<RtEntity>> CreateUpdateDelete()
    {
        var created = new RtEntity(DocumentType, OctoObjectId.GenerateNewId());
        var updated = new RtEntity(DocumentType, new OctoObjectId("000000000000000000000002"));
        var deletedId = new RtEntityId(DocumentType, new OctoObjectId("000000000000000000000003"));
        return
        [
            EntityUpdateInfo<RtEntity>.CreateInsert(DocumentType, created),
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(DocumentType, updated.RtId), updated),
            EntityUpdateInfo<RtEntity>.CreateDelete(deletedId)
        ];
    }

    private void GivenTheGuardRefuses()
    {
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .ThrowsAsync(CrossTenantAccessDeniedException.Forbidden("transformations/0", "Sibling",
                CrossTenantRefusalReason.IdentityNotProvable));
    }

    private void AssertOwnTenantNeverWritten()
    {
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    private void AssertTargetNeverWritten()
    {
        A.CallTo(() => _targetRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustNotHaveHappened();
        A.CallTo(() => _access.OpenSessionAsync()).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller)]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    public async Task WithTarget_CreateUpdateAndDelete_GoOnlyToTheTarget_WithTheSessionTheGuardProved(
        NodeExecutionIdentity identity)
    {
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath, TargetTenant = Sibling, Identity = identity
        };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.OpenTenantAsync(Sibling, identity, dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _targetRepository.ApplyChangesAsync(_targetSession,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>.That.Matches(u =>
                    u.Count() == 3 &&
                    u.Any(x => x.ModOption == EntityModOptions.Insert) &&
                    u.Any(x => x.ModOption == EntityModOptions.Update) &&
                    u.Any(x => x.ModOption == EntityModOptions.Delete)),
                A<IReadOnlyList<AssociationUpdateInfo>>._, A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _targetSession.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        AssertOwnTenantNeverWritten();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WithTarget_TheBatchIsJudgedWithTheTargetsCkModel()
    {
        var cache = ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes();
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath, TargetTenant = Sibling };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete());

        await CreateNode(next, cache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => cache.IsTenantLoaded(TargetTenantId)).MustHaveHappened();
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(TargetTenantId, DocumentType, out ignored)).MustHaveHappened();
        A.CallTo(() => cache.IsTenantLoaded(A<string>.That.Not.IsEqualTo(TargetTenantId))).MustNotHaveHappened();
    }

    [Fact]
    public async Task Refused_FailsTheNode_BeforeAnythingIsWritten_InEitherTenant()
    {
        GivenTheGuardRefuses();
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath, TargetTenant = Sibling };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete());

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantAccessCodes.Forbidden, ex.ErrorCode);
        AssertTargetNeverWritten();
        AssertOwnTenantNeverWritten();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Refused_InADryRun_FailsTheNode_AndRecordsNoIntent()
    {
        GivenTheGuardRefuses();
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath, TargetTenant = Sibling };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete(),
            new DefaultPipelineExecutionMode { IsDryRun = true });

        await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertTargetNeverWritten();
        AssertOwnTenantNeverWritten();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WithTarget_TheRunsMethodMarkerNeverAppliesInTheTarget_AReplacedCreateIsRefused()
    {
        // The target's CK model replaces the generic create of the type by a method, and the run carries the marker
        // of exactly that method's own run. In its own tenant that insert would pass; in another tenant it never does.
        ReplacedCreateTestSupport.ConfigureRun(EtlContext, ReplacedCreateTestSupport.OwnRunMarker());
        A.CallTo(() => EtlContext.TenantId).Returns("companya");
        A.CallTo(() => _access.TenantId).Returns(ReplacedCreateTestSupport.Tenant);
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath,
            TargetTenant = new TenantTargetConfiguration
                { Relation = TenantRelation.Sibling, TenantId = ReplacedCreateTestSupport.Tenant }
        };
        var (dataContext, nodeContext, next) =
            Prepare(config, [ReplacedCreateTestSupport.Insert(ReplacedCreateTestSupport.ReplacedType)]);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            CreateNode(next, ReplacedCreateTestSupport.CkCache()).ProcessObjectAsync(dataContext, nodeContext));

        AssertTargetNeverWritten();
        AssertOwnTenantNeverWritten();
    }

    [Fact]
    public async Task WithoutTarget_TheRunsMethodMarkerStillAppliesInTheOwnTenant()
    {
        // Control of the test above: the same insert in the own tenant, in the method's own run, is allowed.
        ReplacedCreateTestSupport.ConfigureRun(EtlContext, ReplacedCreateTestSupport.OwnRunMarker());
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) =
            Prepare(config, [ReplacedCreateTestSupport.Insert(ReplacedCreateTestSupport.ReplacedType)]);

        await CreateNode(next, ReplacedCreateTestSupport.CkCache()).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WithTarget_AnEngineErrorInTheTarget_FailsTheExecution_AndNamesTheTarget()
    {
        // AB#9770 (decision 6): the target's engine refuses the batch (validation error in the operation result). In
        // the own tenant this is only logged; for another tenant it must fail the execution.
        A.CallTo(() => _targetRepository.ApplyChangesAsync(_targetSession,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                    IReadOnlyList<AssociationUpdateInfo> _, OperationResult or) =>
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error")));
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath, TargetTenant = Sibling };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete());

        var failed = await Assert.ThrowsAsync<CrossTenantWriteFailedException>(() =>
            CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantWriteCodes.WriteFailed, failed.ErrorCode);
        var failure = Assert.Single(failed.Failures);
        Assert.Equal(TargetTenantId, failure.TenantId);
        Assert.Equal("VALIDATION_FAILED", failure.ErrorCode);
        Assert.Contains($"'{TargetTenantId}'", failed.Message);
        A.CallTo(() => _targetSession.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => _targetSession.AbortTransactionAsync()).MustHaveHappened();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    private static AssociationUpdateInfo Association(RtEntityId origin, RtEntityId target) =>
        AssociationUpdateInfo.CreateInsert(origin, target, new RtCkId<CkAssociationRoleId>("TestModel/RelatesTo"));

    private void GivenTheTargetHolds(params OctoObjectId[] rtIds)
    {
        A.CallTo(() => _targetRepository.GetRtEntitiesByIdAsync(_targetSession, A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> type, IReadOnlyList<OctoObjectId> ids,
                    RtEntityQueryOptions _, int? _, int? _) =>
                Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>(
                    ids.Where(rtIds.Contains).Select(id => new RtEntity(type, id)).ToList(), rtIds.Length, null, null)));
    }

    [Fact]
    public async Task WithTarget_AnAssociationToAnEntityTheTargetDoesNotHold_IsRefused_BeforeAnythingIsWritten()
    {
        // Abuse case "cross-tenant association": the new document in B would point at an RtId of company A.
        var document = new RtEntity(DocumentType, OctoObjectId.GenerateNewId());
        var invoiceOfA = new RtEntityId(new RtCkId<CkTypeId>("TestModel/Invoice"), OctoObjectId.GenerateNewId());
        GivenTheTargetHolds();
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath, AssociationUpdatesPath = AssociationUpdatesPath,
            TargetTenant = Sibling
        };
        var (dataContext, nodeContext, next) =
            Prepare(config, [EntityUpdateInfo<RtEntity>.CreateInsert(DocumentType, document)]);
        A.CallTo(() => dataContext.Get<List<AssociationUpdateInfo>>(AssociationUpdatesPath))
            .Returns([Association(new RtEntityId(DocumentType, document.RtId), invoiceOfA)]);

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantRefusalReason.CrossTenantAssociation, ex.ReasonCode);
        A.CallTo(() => _targetRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustNotHaveHappened();
        AssertOwnTenantNeverWritten();
    }

    [Fact]
    public async Task WithTarget_AnAssociationBetweenANewEntityAndOneTheTargetHolds_IsWritten()
    {
        var document = new RtEntity(DocumentType, OctoObjectId.GenerateNewId());
        var supplierInB = new RtEntityId(new RtCkId<CkTypeId>("TestModel/Supplier"), OctoObjectId.GenerateNewId());
        GivenTheTargetHolds(supplierInB.RtId);
        var config = new ApplyChangesNodeConfiguration2
        {
            EntityUpdatesPath = EntityUpdatesPath, AssociationUpdatesPath = AssociationUpdatesPath,
            TargetTenant = Sibling
        };
        var (dataContext, nodeContext, next) =
            Prepare(config, [EntityUpdateInfo<RtEntity>.CreateInsert(DocumentType, document)]);
        A.CallTo(() => dataContext.Get<List<AssociationUpdateInfo>>(AssociationUpdatesPath))
            .Returns([Association(new RtEntityId(DocumentType, document.RtId), supplierInB)]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _targetRepository.ApplyChangesAsync(_targetSession,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<IReadOnlyList<AssociationUpdateInfo>>.That.Matches(a => a.Count == 1), A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
        AssertOwnTenantNeverWritten();
    }

    [Fact]
    public async Task WithoutTarget_WritesTheOwnTenantAsBefore_AndTheGuardIsNotAsked()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = Prepare(config, CreateUpdateDelete());

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        AssertScopedSessionOpened();
        A.CallTo(() => TenantRepository.ApplyChangesAsync(Session,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappenedOnceExactly();
        AssertTargetNeverWritten();
    }
}

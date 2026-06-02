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

public class ApplyChangesNode2Tests : NodeTestBase
{
    private const string EntityUpdatesPath = "$.entityUpdates";
    private const string AssociationUpdatesPath = "$.associationUpdates";

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public ApplyChangesNode2Tests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private ApplyChangesNode2 CreateNode(NodeDelegate next)
    {
        return new ApplyChangesNode2(next, _etlContext);
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

    [Fact]
    public async Task ProcessObjectAsync_WithEntityUpdates_CommitsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
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

        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
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

        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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

        A.CallTo(() => _tenantRepository.GetSessionAsync()).MustNotHaveHappened();
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

        A.CallTo(() => _tenantRepository.GetSessionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = EntityUpdatesPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration2>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupEntityData(dataContext, EntityUpdatesPath, data);

        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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

        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustNotHaveHappened();
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
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
}

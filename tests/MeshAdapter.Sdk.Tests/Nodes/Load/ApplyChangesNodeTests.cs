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

public class ApplyChangesNodeTests : SessionNodeTestBase
{
    private const string DataPath = "$.updateInfos";


    public ApplyChangesNodeTests()
    {

        GivenSystemSessionIsExpected();
    }

    private ApplyChangesNode CreateNode(NodeDelegate next)
    {
        return new ApplyChangesNode(next, EtlContext);
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

    private static EntityUpdateInfo<RtEntity> CreateDeleteUpdateInfo(string? rtId = null)
    {
        var rtEntityId = new RtEntityId(
            new RtCkId<CkTypeId>("TestModel/TestType"),
            new OctoObjectId(rtId ?? "000000000000000000000099"));
        return EntityUpdateInfo<RtEntity>.CreateDelete(rtEntityId);
    }

    private static void SetupDataContextList(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(path))
            .Returns(data);
    }

    /// <summary>
    ///     ApplyChanges@1 is the frozen, deprecated twin of @2 and stays on the system context by
    ///     decision (AB#5028) — a pipeline still on @1 must not start stamping or filtering because
    ///     the adapter was upgraded.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_DeliberatelyOpensASystemSession()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertSystemSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_StartsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.StartTransaction()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CommitsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInsertData_AppliesAllInserts()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateInsertUpdateInfo("000000000000000000000002")
        };
        SetupDataContextList(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, OperationResult _) =>
                capturedUpdates = updates);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdates);
        Assert.Equal(2, capturedUpdates!.Count);
        Assert.All(capturedUpdates, u => Assert.Equal(EntityModOptions.Insert, u.ModOption));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateUpdates_KeepsLastUpdatePerEntity()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateUpdateUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000001")
        };
        SetupDataContextList(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, OperationResult _) =>
                capturedUpdates = updates);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdates);
        Assert.Single(capturedUpdates!);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMixedInsertAndUpdate_KeepsAllInsertsAndDedupsUpdates()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000002"),
            CreateUpdateUpdateInfo("000000000000000000000002")
        };
        SetupDataContextList(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, OperationResult _) =>
                capturedUpdates = updates);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdates);
        Assert.Equal(2, capturedUpdates!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDeleteAndInsert_KeepsBothInResult()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateDeleteUpdateInfo("000000000000000000000002")
        };
        SetupDataContextList(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, OperationResult _) =>
                capturedUpdates = updates);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdates);
        Assert.Equal(2, capturedUpdates!.Count);
        Assert.Contains(capturedUpdates, u => u.ModOption == EntityModOptions.Insert);
        Assert.Contains(capturedUpdates, u => u.ModOption == EntityModOptions.Delete);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_DoesNotStartTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertNoSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotStartTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, new List<EntityUpdateInfo<RtEntity>>());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertNoSessionOpened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithFatalErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.FatalError, null, 0, "Fatal error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_StillCallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest<ApplyChangesNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContextList(dataContext, DataPath, data);

        A.CallTo(() => TenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }
}

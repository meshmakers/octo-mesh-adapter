using FakeItEasy;
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
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class ApplyChangesNodeTests
{
    private const string DataPath = "$.updateInfos";

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public ApplyChangesNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        ApplyChangesNodeConfiguration config, JToken? testData = null)
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
            "ApplyChanges",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();

        return (dataContext, nodeContext, next);
    }

    private ApplyChangesNode CreateNode(NodeDelegate next)
    {
        return new ApplyChangesNode(next, _etlContext);
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

    private static void SetupDataContext(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(path, A<JsonSerializer>._))
            .Returns(data);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_StartsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.StartTransaction()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CommitsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInsertData_AppliesAllInserts()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateInsertUpdateInfo("000000000000000000000002")
        };
        SetupDataContext(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        // Two updates for the same entity - only the last should be kept
        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateUpdateUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000001")
        };
        SetupDataContext(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateUpdateUpdateInfo("000000000000000000000002"),
            CreateUpdateUpdateInfo("000000000000000000000002") // duplicate update
        };
        SetupDataContext(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, OperationResult _) =>
                capturedUpdates = updates);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdates);
        // 1 insert + 1 deduped update = 2
        Assert.Equal(2, capturedUpdates!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDeleteAndInsert_KeepsBothInResult()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsertUpdateInfo("000000000000000000000001"),
            CreateDeleteUpdateInfo("000000000000000000000002")
        };
        SetupDataContext(dataContext, DataPath, data);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? capturedUpdates = null;
        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetSessionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotStartTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, new List<EntityUpdateInfo<RtEntity>>());

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tenantRepository.GetSessionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithFatalErrors_AbortsTransaction()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.FatalError, null, 0, "Fatal error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_CallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOperationErrors_StillCallsNext()
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>> { CreateInsertUpdateInfo() };
        SetupDataContext(dataContext, DataPath, data);

        A.CallTo(() => _tenantRepository.ApplyChangesAsync(
                A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _, OperationResult or) =>
            {
                or.AddMessage(new OperationMessage(MessageLevel.Error, null, 0, "Test error"));
            });

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

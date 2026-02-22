using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class FilterLatestUpdateInfoNodeTests
{
    private const string DataPath = "$.updateInfos";
    private const string TargetPath = "$.filtered";

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        FilterLatestUpdateInfoNodeConfiguration config, JToken? testData = null)
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
            "FilterLatestUpdateInfo",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private static RtEntity CreateRtEntity(string rtId, DateTime? changedDateTime = null)
    {
        var ckTypeId = new RtCkId<CkTypeId>("TestModel/TestType");
        var entity = new RtEntity(ckTypeId, new OctoObjectId(rtId))
        {
            RtChangedDateTime = changedDateTime
        };
        return entity;
    }

    private static EntityUpdateInfo<RtEntity> CreateInsert(string rtId)
    {
        var entity = CreateRtEntity(rtId);
        return EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("TestModel/TestType"), entity);
    }

    private static EntityUpdateInfo<RtEntity> CreateUpdate(string rtId, DateTime? changedDateTime = null)
    {
        var entity = CreateRtEntity(rtId, changedDateTime);
        var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>("TestModel/TestType"), entity.RtId);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, entity);
    }

    private static EntityUpdateInfo<RtEntity> CreateDelete(string rtId, DateTime? changedDateTime = null)
    {
        var entity = CreateRtEntity(rtId, changedDateTime);
        var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>("TestModel/TestType"), entity.RtId);
        return EntityUpdateInfo<RtEntity>.CreateDelete(rtEntityId);
    }

    private static void SetupDataContext(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(path, A<JsonSerializer>._))
            .Returns(data);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInserts_KeepsAllInserts()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsert("000000000000000000000001"),
            CreateInsert("000000000000000000000002")
        };
        SetupDataContext(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>>.That.Matches(l => l.Count == 2),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateUpdates_KeepsLatestPerEntity()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateUpdate("000000000000000000000001", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateUpdate("000000000000000000000001", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        SetupDataContext(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>>.That.Matches(l => l.Count == 1),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMixedOperations_KeepsInsertAndLatestUpdateAndLatestDelete()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsert("000000000000000000000001"),
            CreateUpdate("000000000000000000000002", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateUpdate("000000000000000000000002", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateDelete("000000000000000000000003")
        };
        SetupDataContext(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // 1 insert + 1 latest update + 1 delete = 3
        A.CallTo(() => dataContext.SetValueByPath(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>>.That.Matches(l => l.Count == 3),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_DoesNotSetValue()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                A<string>._,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotSetValue()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, new List<EntityUpdateInfo<RtEntity>>());

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                A<string>._,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AlwaysCallsNext()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        SetupDataContext(dataContext, DataPath, null);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class FilterLatestUpdateInfoNodeTests : NodeTestBase
{
    private const string DataPath = "$.updateInfos";
    private const string TargetPath = "$.filtered";

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

    private static void SetupDataContextList(IDataContext dataContext, string path,
        List<EntityUpdateInfo<RtEntity>>? data)
    {
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(path))
            .Returns(data);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInserts_KeepsAllInserts()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsert("000000000000000000000001"),
            CreateInsert("000000000000000000000002")
        };
        SetupDataContextList(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>?>.That.Matches(l => l != null && l.Count == 2),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateUpdates_KeepsLatestPerEntity()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateUpdate("000000000000000000000001", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateUpdate("000000000000000000000001", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        SetupDataContextList(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>?>.That.Matches(l => l != null && l.Count == 1),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMixedOperations_KeepsInsertAndLatestUpdateAndLatestDelete()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            CreateInsert("000000000000000000000001"),
            CreateUpdate("000000000000000000000002", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateUpdate("000000000000000000000002", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateDelete("000000000000000000000003")
        };
        SetupDataContextList(dataContext, DataPath, data);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // 1 insert + 1 latest update + 1 delete = 3
        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>?>.That.Matches(l => l != null && l.Count == 3),
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_DoesNotSetValue()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, null);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                A<string>._,
                A<List<EntityUpdateInfo<RtEntity>>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyData_DoesNotSetValue()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, new List<EntityUpdateInfo<RtEntity>>());

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                A<string>._,
                A<List<EntityUpdateInfo<RtEntity>>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AlwaysCallsNext()
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);

        SetupDataContextList(dataContext, DataPath, null);

        var node = new FilterLatestUpdateInfoNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }
}

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

    private static RtEntity CreateRtEntityWithAttributes(string rtId, DateTime? changed, params (string, object?)[] attributes)
    {
        var entity = CreateRtEntity(rtId, changed);
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        return entity;
    }

    private async Task<List<EntityUpdateInfo<RtEntity>>> RunAsync(List<EntityUpdateInfo<RtEntity>> data)
    {
        var config = new FilterLatestUpdateInfoNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<FilterLatestUpdateInfoNodeConfiguration>(config);
        SetupDataContextList(dataContext, DataPath, data);

        List<EntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<List<EntityUpdateInfo<RtEntity>>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, List<EntityUpdateInfo<RtEntity>>? l, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => captured = l);

        await new FilterLatestUpdateInfoNode(next).ProcessObjectAsync(dataContext, nodeContext);
        Assert.NotNull(captured);
        return captured!;
    }

    private static EntityUpdateInfo<RtEntity> InsertWith(string rtId, DateTime? changed, params (string, object?)[] attributes)
    {
        return EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("TestModel/TestType"),
            CreateRtEntityWithAttributes(rtId, changed, attributes));
    }

    private static EntityUpdateInfo<RtEntity> UpdateWith(string rtId, DateTime? changed, params (string, object?)[] attributes)
    {
        var entity = CreateRtEntityWithAttributes(rtId, changed, attributes);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(
            new RtEntityId(new RtCkId<CkTypeId>("TestModel/TestType"), entity.RtId), entity);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateInsertsOfSameRtId_KeepsLatestInsert()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 1)),
            InsertWith("000000000000000000000001", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 2)),
            InsertWith("000000000000000000000001", new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 3))
        });

        var single = Assert.Single(result);
        Assert.Equal(EntityModOptions.Insert, single.ModOption);
        Assert.Equal(2, single.RtEntity!.Attributes["value"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDuplicateInsertsWithoutTimestamps_LastInBatchWins()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", null, ("value", 1)),
            InsertWith("000000000000000000000001", null, ("value", 2))
        });

        var single = Assert.Single(result);
        Assert.Equal(2, single.RtEntity!.Attributes["value"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInsertAndUpdatesOfSameRtId_ReturnsOneInsertWithLatestValues()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ("value", 1), ("name", "first")),
            UpdateWith("000000000000000000000001", new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 2)),
            UpdateWith("000000000000000000000001", new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 3))
        });

        var single = Assert.Single(result);
        Assert.Equal(EntityModOptions.Insert, single.ModOption);
        Assert.Equal(3, single.RtEntity!.Attributes["value"]);
        Assert.Equal("first", single.RtEntity.Attributes["name"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInsertsOfDifferentRtIds_KeepsAllUntouched()
    {
        var a = InsertWith("000000000000000000000001", null, ("value", 1));
        var b = InsertWith("000000000000000000000002", null, ("value", 2));

        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>> { a, b });

        Assert.Equal(2, result.Count);
        Assert.Same(a, result[0]);
        Assert.Same(b, result[1]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUpdateOfOtherRtIdThanInsert_KeepsBoth()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", null, ("value", 1)),
            UpdateWith("000000000000000000000002", null, ("value", 2))
        });

        Assert.Equal(2, result.Count);
        Assert.Equal(EntityModOptions.Insert, result[0].ModOption);
        Assert.Equal(EntityModOptions.Update, result[1].ModOption);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithTimestampedInsertAndLaterUpdateWithoutTimestamp_UpdateWins()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), ("value", 1)),
            UpdateWith("000000000000000000000001", null, ("value", 2))
        });

        var single = Assert.Single(result);
        Assert.Equal(EntityModOptions.Insert, single.ModOption);
        Assert.Equal(2, single.RtEntity!.Attributes["value"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithOlderInsertAttributes_DoesNotLeakThemIntoLatestInsert()
    {
        var result = await RunAsync(new List<EntityUpdateInfo<RtEntity>>
        {
            InsertWith("000000000000000000000001", null, ("value", 1), ("stale", "old")),
            InsertWith("000000000000000000000001", null, ("value", 2))
        });

        var single = Assert.Single(result);
        Assert.Equal(2, single.RtEntity!.Attributes["value"]);
        Assert.False(single.RtEntity.Attributes.ContainsKey("stale"));
    }
}

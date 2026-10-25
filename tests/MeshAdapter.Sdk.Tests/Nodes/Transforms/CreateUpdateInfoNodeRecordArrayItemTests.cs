using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// AB#7442: <c>CreateUpdateInfo@1</c> emits atomic RecordArray item updates (no read of the entity).
/// </summary>
public class CreateUpdateInfoNodeRecordArrayItemTests
{
    private const string TestTenantId = "test-tenant";
    private static readonly RtCkId<CkTypeId> TestRtCkTypeId = new("TestModel/TestType");
    private static readonly OctoObjectId TestRtId = new("000000000000000000000001");

    private static CreateUpdateInfoNodeConfiguration Config(params RecordArrayItemUpdateConfiguration[] items) => new()
    {
        UpdateKind = UpdateKind.Update,
        RtId = TestRtId,
        CkTypeId = TestRtCkTypeId,
        TargetPath = "$.result",
        RecordArrayItemUpdates = items
    };

    private static RecordArrayItemUpdateConfiguration Item(string? updatesPath = null, string? matchValue = null,
        string? matchValuePath = null, params RecordAttributeUpdate[] updates) => new()
    {
        AttributeName = "States",
        MatchAttributeName = "ExternalId",
        MatchValue = matchValue,
        MatchValuePath = matchValuePath,
        UpdatesPath = updatesPath,
        AttributeUpdates = updates
    };

    private static async Task<(EntityUpdateInfo<RtEntity>? Update, IPipelineLogger Logger)> RunAsync(
        CreateUpdateInfoNodeConfiguration config, JsonNode data)
    {
        var logger = A.Fake<IPipelineLogger>();
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => etlContext.TenantId).Returns(TestTenantId);

        IDataContext real = new DataContextImpl(JsonDocument.Parse(data.ToJsonString()));
        var rootNodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger, real);
        var nodeContext = rootNodeContext.RegisterChildNode("CreateUpdateInfo", 0, config, real);
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        EntityUpdateInfo<RtEntity>? captured = null;
        A.CallTo(() => dataContext.Set("$.result", A<EntityUpdateInfo<RtEntity>?>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => captured = u);

        var node = new CreateUpdateInfoNode(A.Fake<NodeDelegate>(), etlContext, A.Fake<ICkCacheService>());
        await node.ProcessObjectAsync(dataContext, nodeContext);
        return (captured, logger);
    }

    private static string[] Errors(IPipelineLogger logger) => Fake.GetCalls(logger)
        .Where(c => c.Method.Name == nameof(IPipelineLogger.Error))
        .Select(c => string.Join(" | ", c.Arguments.Select(a => a?.ToString()))).ToArray();

    private static string[] Warnings(IPipelineLogger logger) => Fake.GetCalls(logger)
        .Where(c => c.Method.Name == nameof(IPipelineLogger.Warning))
        .Select(c => string.Join(" | ", c.Arguments.Select(a => a?.ToString()))).ToArray();

    [Fact]
    public async Task SingleItemUpdate_WithValuePathAndStaticValue_ProducesOneItemUpdateWithoutRegularAttributes()
    {
        var config = Config(Item(matchValue: "poll-1", updates:
        [
            new RecordAttributeUpdate { AttributeName = "CurrentValue", ValuePath = "$.value" },
            new RecordAttributeUpdate { AttributeName = "Quality", Value = "good" }
        ]));

        var (update, logger) = await RunAsync(config, new JsonObject { ["value"] = 12.5 });

        Assert.Empty(Errors(logger));
        Assert.NotNull(update);
        Assert.Empty(update!.RtEntity!.Attributes);
        var item = Assert.Single(update.RecordArrayItemUpdates!);
        Assert.Equal("States", item.AttributeName);
        Assert.Equal("ExternalId", item.MatchMember);
        Assert.Equal("poll-1", item.MatchValue);
        Assert.Equal(12.5, item.MemberUpdates["CurrentValue"]);
        Assert.Equal("good", item.MemberUpdates["Quality"]);
        Assert.Equal(EntityModOptions.Update, update.ModOption);
    }

    [Fact]
    public async Task UpdatesPath_ProducesOneItemUpdatePerElement_WithRelativePaths()
    {
        var config = Config(Item(updatesPath: "$.states", matchValuePath: "$.pollUuid", updates:
        [
            new RecordAttributeUpdate { AttributeName = "CurrentValue", ValuePath = "$.value" },
            new RecordAttributeUpdate { AttributeName = "LastUpdate", Value = "=now()" }
        ]));
        var data = new JsonObject
        {
            ["states"] = new JsonArray(
                new JsonObject { ["pollUuid"] = "a", ["value"] = 1 },
                new JsonObject { ["pollUuid"] = "b", ["value"] = "x" })
        };

        var (update, _) = await RunAsync(config, data);

        var items = update!.RecordArrayItemUpdates!.ToList();
        Assert.Equal(["a", "b"], items.Select(i => (string)i.MatchValue));
        Assert.Equal(1, Convert.ToInt32(items[0].MemberUpdates["CurrentValue"]));
        Assert.Equal("x", items[1].MemberUpdates["CurrentValue"]);
        Assert.True(DateTime.TryParse((string)items[0].MemberUpdates["LastUpdate"]!, out _));
    }

    [Fact]
    public async Task ElementWithoutMatchValue_IsSkippedWithAWarning_OthersStay()
    {
        var config = Config(Item(updatesPath: "$.states", matchValuePath: "$.pollUuid", updates:
            [new RecordAttributeUpdate { AttributeName = "CurrentValue", ValuePath = "$.value" }]));
        var data = new JsonObject
        {
            ["states"] = new JsonArray(
                new JsonObject { ["value"] = 1 },
                new JsonObject { ["pollUuid"] = "b", ["value"] = 2 })
        };

        var (update, logger) = await RunAsync(config, data);

        Assert.Equal("b", Assert.Single(update!.RecordArrayItemUpdates!).MatchValue);
        Assert.Single(Warnings(logger));
    }

    [Fact]
    public async Task NoItemProducedAndNoRegularAttributes_EmitsNoUpdate()
    {
        var config = Config(Item(updatesPath: "$.missing", matchValuePath: "$.k",
            updates: [new RecordAttributeUpdate { AttributeName = "CurrentValue", Value = 1 }]));

        var (update, logger) = await RunAsync(config, new JsonObject());

        Assert.Null(update);
        Assert.Single(Warnings(logger));
    }

    [Fact]
    public async Task ItemUpdates_AreOnlyAllowedForUpdate()
    {
        var config = Config(Item(matchValue: "a",
            updates: [new RecordAttributeUpdate { AttributeName = "CurrentValue", Value = 1 }]));
        config.UpdateKind = UpdateKind.Insert;

        var (update, logger) = await RunAsync(config, new JsonObject());

        Assert.Null(update);
        Assert.Contains(Errors(logger), e => e.Contains("only supported with UpdateKind UPDATE"));
    }

    [Fact]
    public async Task ExistingConfiguration_WithoutItemUpdates_KeepsFailingOnMissingAttributeUpdates()
    {
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update, RtId = TestRtId, CkTypeId = TestRtCkTypeId, TargetPath = "$.result"
        };

        var (update, logger) = await RunAsync(config, new JsonObject());

        Assert.Null(update);
        Assert.Contains(Errors(logger), e => e.Contains("AttributeUpdates is not set"));
    }

    [Fact]
    public async Task ItemUpdateInfo_SurvivesTheDataContext_AsSystemTextJson()
    {
        var config = Config(Item(matchValue: "a", updates:
            [new RecordAttributeUpdate { AttributeName = "CurrentValue", Value = 7 }]));
        var (update, _) = await RunAsync(config, new JsonObject());

        var json = JsonSerializer.Serialize(update, Meshmakers.Octo.Runtime.Contracts.Serialization.RtSystemTextJsonSerializer.Default);
        var clone = JsonSerializer.Deserialize<EntityUpdateInfo<RtEntity>>(json,
            Meshmakers.Octo.Runtime.Contracts.Serialization.RtSystemTextJsonSerializer.Default)!;

        var item = Assert.Single(clone.RecordArrayItemUpdates!);
        Assert.Equal("a", item.MatchValue is JsonElement e ? e.GetString() : item.MatchValue);
        Assert.Equal(7, Convert.ToInt32(item.MemberUpdates["CurrentValue"] is JsonElement m ? m.GetInt32() : item.MemberUpdates["CurrentValue"]));
    }
}

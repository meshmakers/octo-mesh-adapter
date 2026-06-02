using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Transforms;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class DistinctNodeTests : NodeTestBase
{
    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        DistinctNodeConfiguration config,
        JsonNode? testData)
    {
        // Round-trip test data through JSON so JsonValue instances are backed by JsonElements
        // (DistinctNode.ConvertNodeToValue calls JsonValue.GetValue<JsonElement>()).
        JsonNode? normalized = testData is null
            ? null
            : JsonNode.Parse(testData.ToJsonString());

        var setup = PrepareTest<DistinctNodeConfiguration>(config, normalized);

        if (normalized is JsonObject obj && obj.TryGetPropertyValue(config.Path.TrimStart('$', '.'), out var arrayNode))
        {
            A.CallTo(() => setup.DataContext.GetKind(config.Path))
                .Returns(arrayNode is JsonArray ? DataKind.Array : DataKind.Undefined);
            if (arrayNode is JsonArray arr)
            {
                A.CallTo(() => setup.DataContext.Get<JsonArray>(config.Path))
                    .ReturnsLazily(() => (JsonArray)JsonNode.Parse(arr.ToJsonString())!);
            }
        }
        else
        {
            A.CallTo(() => setup.DataContext.GetKind(config.Path)).Returns(DataKind.Undefined);
        }

        return setup;
    }

    private static JsonArray? CapturedTarget(IDataContext dataContext, DistinctNodeConfiguration config)
    {
        JsonArray? captured = null;
        A.CallTo(() => dataContext.Set(
                config.TargetPath, A<JsonArray?>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes((string _, JsonArray? result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) =>
            {
                captured = result;
            });
        return captured;
    }

    private static JsonArray? GetCapturedFromCalls(IDataContext dataContext, DistinctNodeConfiguration config)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == config.TargetPath);
        return call?.Arguments[1] as JsonArray;
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByStringId_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["name"] = "Item 1" },
                new JsonObject { ["id"] = "B", ["name"] = "Item 2" },
                new JsonObject { ["id"] = "A", ["name"] = "Item 3" },
                new JsonObject { ["id"] = "C", ["name"] = "Item 4" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByIntegerId_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = 1, ["name"] = "Item 1" },
                new JsonObject { ["id"] = 2, ["name"] = "Item 2" },
                new JsonObject { ["id"] = 1, ["name"] = "Item 3" },
                new JsonObject { ["id"] = 3, ["name"] = "Item 4" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByBooleanFlag_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "active"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = 1, ["active"] = true },
                new JsonObject { ["id"] = 2, ["active"] = false },
                new JsonObject { ["id"] = 3, ["active"] = true },
                new JsonObject { ["id"] = 4, ["active"] = false })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByDoubleValue_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.readings",
            TargetPath = "$.distinctReadings",
            DistinctValuePath = "value"
        };

        var testData = new JsonObject
        {
            ["readings"] = new JsonArray(
                new JsonObject { ["sensor"] = "A", ["value"] = 1.5 },
                new JsonObject { ["sensor"] = "B", ["value"] = 2.7 },
                new JsonObject { ["sensor"] = "C", ["value"] = 1.5 },
                new JsonObject { ["sensor"] = "D", ["value"] = 3.0 })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByDateTime_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.events",
            TargetPath = "$.distinctEvents",
            DistinctValuePath = "date"
        };

        var testData = new JsonObject
        {
            ["events"] = new JsonArray(
                new JsonObject { ["name"] = "E1", ["date"] = "2024-01-15T10:00:00Z" },
                new JsonObject { ["name"] = "E2", ["date"] = "2024-02-20T15:30:00Z" },
                new JsonObject { ["name"] = "E3", ["date"] = "2024-01-15T10:00:00Z" },
                new JsonObject { ["name"] = "E4", ["date"] = "2024-03-01T08:00:00Z" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_DoesNotCallSetValue()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray()
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
                config.TargetPath, A<JsonArray?>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullData_DoesNotCallSetValue()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, null);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
                config.TargetPath, A<JsonArray?>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoDuplicates_ReturnsAll()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "A" },
                new JsonObject { ["id"] = "B" },
                new JsonObject { ["id"] = "C" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task ProcessObjectAsync_AllDuplicates_ReturnsOneItem()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "X", ["name"] = "First" },
                new JsonObject { ["id"] = "X", ["name"] = "Second" },
                new JsonObject { ["id"] = "X", ["name"] = "Third" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public async Task ProcessObjectAsync_KeepsFirstOccurrence()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "id"
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["name"] = "First A" },
                new JsonObject { ["id"] = "A", ["name"] = "Second A" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("First A", result![0]?["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_ScalarArray_DeduplicatesDirectly()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.uuids",
            TargetPath = "$.uniqueUuids"
        };

        var testData = new JsonObject
        {
            ["uuids"] = new JsonArray("aaa", "bbb", "aaa", "ccc", "bbb")
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedFromCalls(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }
}

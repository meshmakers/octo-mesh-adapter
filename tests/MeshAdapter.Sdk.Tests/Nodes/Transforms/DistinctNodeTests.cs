using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Transforms;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class DistinctNodeTests
{
    private (IDataContext, INodeContext, List<JToken>?) PrepareTest(DistinctNodeConfiguration config, JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData);

        // Capture results written to the target path
        List<JToken>? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<object>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, object result) =>
            {
                if (result is List<JToken> list)
                    capturedResult = list;
                else if (result is IEnumerable<JToken> enumerable)
                    capturedResult = enumerable.ToList();
            });

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("Distinct", 0, config, dataContext);

        return (dataContext, nodeContext, capturedResult);
    }

    private List<JToken>? GetCapturedResult(IDataContext dataContext, DistinctNodeConfiguration config)
    {
        List<JToken>? result = null;
        // Extract captured result from the fake's recorded calls
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "SetValueByPath" && c.Arguments.Count == 5
                                 && c.Arguments[0] as string == config.TargetPath);
        if (call?.Arguments[4] is List<JToken> list)
            result = list;
        else if (call?.Arguments[4] is IEnumerable<JToken> enumerable)
            result = enumerable.ToList();
        return result;
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = "A", ["name"] = "Item 1" },
                new JObject { ["id"] = "B", ["name"] = "Item 2" },
                new JObject { ["id"] = "A", ["name"] = "Item 3" },
                new JObject { ["id"] = "C", ["name"] = "Item 4" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = 1, ["name"] = "Item 1" },
                new JObject { ["id"] = 2, ["name"] = "Item 2" },
                new JObject { ["id"] = 1, ["name"] = "Item 3" },
                new JObject { ["id"] = 3, ["name"] = "Item 4" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = 1, ["active"] = true },
                new JObject { ["id"] = 2, ["active"] = false },
                new JObject { ["id"] = 3, ["active"] = true },
                new JObject { ["id"] = 4, ["active"] = false }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
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

        var testData = new JObject
        {
            ["readings"] = new JArray(
                new JObject { ["sensor"] = "A", ["value"] = 1.5 },
                new JObject { ["sensor"] = "B", ["value"] = 2.7 },
                new JObject { ["sensor"] = "C", ["value"] = 1.5 },
                new JObject { ["sensor"] = "D", ["value"] = 3.0 }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
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

        var testData = new JObject
        {
            ["events"] = new JArray(
                new JObject { ["name"] = "E1", ["date"] = "2024-01-15T10:00:00Z" },
                new JObject { ["name"] = "E2", ["date"] = "2024-02-20T15:30:00Z" },
                new JObject { ["name"] = "E3", ["date"] = "2024-01-15T10:00:00Z" },
                new JObject { ["name"] = "E4", ["date"] = "2024-03-01T08:00:00Z" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
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

        var testData = new JObject
        {
            ["items"] = new JArray()
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, _) = PrepareTest(config, null);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = "A" },
                new JObject { ["id"] = "B" },
                new JObject { ["id"] = "C" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = "X", ["name"] = "First" },
                new JObject { ["id"] = "X", ["name"] = "Second" },
                new JObject { ["id"] = "X", ["name"] = "Third" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Single(result);
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

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["id"] = "A", ["name"] = "First A" },
                new JObject { ["id"] = "A", ["name"] = "Second A" }
            )
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("First A", result[0]["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_ScalarArray_DeduplicatesDirectly()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.uuids",
            TargetPath = "$.uniqueUuids"
        };

        var testData = new JObject
        {
            ["uuids"] = new JArray("aaa", "bbb", "aaa", "ccc", "bbb")
        };

        var (dataContext, nodeContext, _) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = GetCapturedResult(dataContext, config);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
    }
}

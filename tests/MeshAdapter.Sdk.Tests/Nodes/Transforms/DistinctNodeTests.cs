using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Transforms;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class DistinctNodeTests
{
    private (IDataContext, INodeContext) PrepareTest(DistinctNodeConfiguration config, JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData);

        // Capture calls to GetComplexObjectByPath and return the test data at the source path
        A.CallTo(() => dataContext.GetComplexObjectByPath<List<object>>(config.Path, A<Newtonsoft.Json.JsonSerializer>._))
            .ReturnsLazily(() =>
            {
                if (testData == null) return null;
                var token = testData.SelectToken(config.Path);
                if (token is JArray array)
                {
                    return array.Cast<object>().ToList();
                }
                return null;
            });

        // Capture results written to the target path
        List<object>? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, List<object> result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("Distinct", 0, config, dataContext);

        return (dataContext, nodeContext);
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

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 3),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 3),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByDoubleValue_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.measurements",
            TargetPath = "$.distinctMeasurements",
            DistinctValuePath = "value"
        };

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = 10.5, ["timestamp"] = "2023-01-01" },
                new JObject { ["value"] = 20.7, ["timestamp"] = "2023-01-02" },
                new JObject { ["value"] = 10.5, ["timestamp"] = "2023-01-03" },
                new JObject { ["value"] = 30.2, ["timestamp"] = "2023-01-04" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 3),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_DistinctByBooleanValue_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.flags",
            TargetPath = "$.distinctFlags",
            DistinctValuePath = "active"
        };

        var testData = new JObject
        {
            ["flags"] = new JArray(
                new JObject { ["active"] = true, ["name"] = "Flag 1" },
                new JObject { ["active"] = false, ["name"] = "Flag 2" },
                new JObject { ["active"] = true, ["name"] = "Flag 3" },
                new JObject { ["active"] = false, ["name"] = "Flag 4" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 2),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
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
                new JObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 1" },
                new JObject { ["date"] = new DateTime(2023, 1, 2), ["name"] = "Event 2" },
                new JObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 3" },
                new JObject { ["date"] = new DateTime(2023, 1, 3), ["name"] = "Event 4" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 3),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NestedPath_RemovesDuplicates()
    {
        var config = new DistinctNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.distinctItems",
            DistinctValuePath = "metadata.id"
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["metadata"] = new JObject { ["id"] = "A" }, ["name"] = "Item 1" },
                new JObject { ["metadata"] = new JObject { ["id"] = "B" }, ["name"] = "Item 2" },
                new JObject { ["metadata"] = new JObject { ["id"] = "A" }, ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 2),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoDuplicates_ReturnsAllItems()
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
                new JObject { ["id"] = "C", ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 3),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_DoesNotWriteResult()
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

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<List<object>>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._, A<Newtonsoft.Json.JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingDistinctValue_SkipsItems()
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
                new JObject { ["name"] = "Item 2" }, // Missing id
                new JObject { ["id"] = "B", ["name"] = "Item 3" },
                new JObject { ["name"] = "Item 4" } // Missing id
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 2),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
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
                new JObject { ["id"] = "A", ["name"] = "Item 1" },
                new JObject { ["id"] = "A", ["name"] = "Item 2" },
                new JObject { ["id"] = "A", ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>.That.Matches(l => l.Count == 1),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode,
                A<Newtonsoft.Json.JsonSerializer>._))
            .MustHaveHappenedOnceExactly();
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
                new JObject { ["id"] = "A", ["name"] = "First" },
                new JObject { ["id"] = "A", ["name"] = "Second" },
                new JObject { ["id"] = "A", ["name"] = "Third" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);

        List<object>? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<List<object>>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, List<object> result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        var next = A.Fake<NodeDelegate>();
        var node = new DistinctNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Single(capturedResult);
        var firstItem = capturedResult[0] as JObject;
        Assert.NotNull(firstItem);
        Assert.Equal("First", firstItem["name"]?.ToString());
    }
}

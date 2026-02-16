using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MinMaxNodeTests
{
    private (IDataContext, INodeContext) PrepareTest(MinMaxNodeConfiguration config, JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData);

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

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("MinMax", 0, config, dataContext);

        return (dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_MinWithIntValues_FindsMinimum()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["value"] = 30, ["name"] = "Item 1" },
                new JObject { ["value"] = 10, ["name"] = "Item 2" },
                new JObject { ["value"] = 20, ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_MaxWithIntValues_FindsMaximum()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Max
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["value"] = 30, ["name"] = "Item 1" },
                new JObject { ["value"] = 10, ["name"] = "Item 2" },
                new JObject { ["value"] = 20, ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 1", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_MinWithDoubleValues_FindsMinimum()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.measurements",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = 10.5, ["name"] = "Measurement 1" },
                new JObject { ["value"] = 5.2, ["name"] = "Measurement 2" },
                new JObject { ["value"] = 20.7, ["name"] = "Measurement 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Measurement 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_MaxWithDateTimeValues_FindsMaximum()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.events",
            TargetPath = "$.result",
            ValuePath = "date",
            Mode = MinMaxMode.Max
        };

        var testData = new JObject
        {
            ["events"] = new JArray(
                new JObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 1" },
                new JObject { ["date"] = new DateTime(2023, 6, 15), ["name"] = "Event 2" },
                new JObject { ["date"] = new DateTime(2023, 3, 10), ["name"] = "Event 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Event 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_MinWithDateTimeValues_FindsMinimum()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.events",
            TargetPath = "$.result",
            ValuePath = "date",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["events"] = new JArray(
                new JObject { ["date"] = new DateTime(2023, 6, 15), ["name"] = "Event 1" },
                new JObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 2" },
                new JObject { ["date"] = new DateTime(2023, 3, 10), ["name"] = "Event 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Event 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_DoesNotWriteResult()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["items"] = new JArray()
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<JObject>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._, A<Newtonsoft.Json.JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingValuePath_SkipsItems()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["name"] = "Item 1" },
                new JObject { ["value"] = 10, ["name"] = "Item 2" },
                new JObject { ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_AllSameValue_ReturnsFirstOccurrence()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["value"] = 10, ["name"] = "First" },
                new JObject { ["value"] = 10, ["name"] = "Second" },
                new JObject { ["value"] = 10, ["name"] = "Third" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("First", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_SingleItem_ReturnsThatItem()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Max
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["value"] = 42, ["name"] = "Only Item" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Only Item", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_NestedValuePath_FindsCorrectObject()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "metadata.score",
            Mode = MinMaxMode.Max
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["metadata"] = new JObject { ["score"] = 50 }, ["name"] = "Item 1" },
                new JObject { ["metadata"] = new JObject { ["score"] = 90 }, ["name"] = "Item 2" },
                new JObject { ["metadata"] = new JObject { ["score"] = 70 }, ["name"] = "Item 3" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        JObject? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<JObject>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, A<Newtonsoft.Json.JsonSerializer>._))
            .Invokes((string _, JObject result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _, Newtonsoft.Json.JsonSerializer _) =>
            {
                capturedResult = result;
            });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult["name"]?.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_NullSource_CallsNextWithoutWriting()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var (dataContext, nodeContext) = PrepareTest(config, null);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<JObject>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._, A<Newtonsoft.Json.JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoComparableValues_DoesNotWriteResult()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value",
            Mode = MinMaxMode.Min
        };

        var testData = new JObject
        {
            ["items"] = new JArray(
                new JObject { ["value"] = "not a number", ["name"] = "Item 1" },
                new JObject { ["value"] = true, ["name"] = "Item 2" }
            )
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new MinMaxNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<JObject>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._, A<Newtonsoft.Json.JsonSerializer>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_DefaultMode_IsMin()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value"
        };

        Assert.Equal(MinMaxMode.Min, config.Mode);
    }
}

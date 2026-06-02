using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MinMaxNodeTests
{
    /// <summary>
    /// Builds a real <see cref="DataContextImpl"/> over the test data, wrapped by FakeItEasy
    /// so that reads (SelectMatches/GetValue) run against the real implementation while Set
    /// calls can be observed. This exercises the genuine path-resolution behavior end-to-end.
    /// </summary>
    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        MinMaxNodeConfiguration config,
        JsonNode? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var data = testData ?? new JsonObject();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(data.ToJsonString()));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("MinMax", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private static void CaptureSet(IDataContext dataContext, MinMaxNodeConfiguration config,
        Action<JsonNode?> capture)
    {
        A.CallTo(() => dataContext.Set(
                config.TargetPath, A<JsonNode?>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes((string _, JsonNode? result, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capture(result));
    }

    private static void VerifyNextCalled(NodeDelegate next, IDataContext dataContext, INodeContext nodeContext)
    {
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    private static void VerifySetNotCalled(IDataContext dataContext)
    {
        A.CallTo(() => dataContext.Set(
                A<string>._, A<JsonNode?>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["value"] = 30, ["name"] = "Item 1" },
                new JsonObject { ["value"] = 10, ["name"] = "Item 2" },
                new JsonObject { ["value"] = 20, ["name"] = "Item 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["value"] = 30, ["name"] = "Item 1" },
                new JsonObject { ["value"] = 10, ["name"] = "Item 2" },
                new JsonObject { ["value"] = 20, ["name"] = "Item 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 1", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = 10.5, ["name"] = "Measurement 1" },
                new JsonObject { ["value"] = 5.2, ["name"] = "Measurement 2" },
                new JsonObject { ["value"] = 20.7, ["name"] = "Measurement 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Measurement 2", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["events"] = new JsonArray(
                new JsonObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 1" },
                new JsonObject { ["date"] = new DateTime(2023, 6, 15), ["name"] = "Event 2" },
                new JsonObject { ["date"] = new DateTime(2023, 3, 10), ["name"] = "Event 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Event 2", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["events"] = new JsonArray(
                new JsonObject { ["date"] = new DateTime(2023, 6, 15), ["name"] = "Event 1" },
                new JsonObject { ["date"] = new DateTime(2023, 1, 1), ["name"] = "Event 2" },
                new JsonObject { ["date"] = new DateTime(2023, 3, 10), ["name"] = "Event 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Event 2", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray()
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        VerifySetNotCalled(dataContext);
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["name"] = "Item 1" },
                new JsonObject { ["value"] = 10, ["name"] = "Item 2" },
                new JsonObject { ["name"] = "Item 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["value"] = 10, ["name"] = "First" },
                new JsonObject { ["value"] = 10, ["name"] = "Second" },
                new JsonObject { ["value"] = 10, ["name"] = "Third" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("First", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["value"] = 42, ["name"] = "Only Item" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Only Item", capturedResult!["name"]?.ToString());
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["metadata"] = new JsonObject { ["score"] = 50 }, ["name"] = "Item 1" },
                new JsonObject { ["metadata"] = new JsonObject { ["score"] = 90 }, ["name"] = "Item 2" },
                new JsonObject { ["metadata"] = new JsonObject { ["score"] = 70 }, ["name"] = "Item 3" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult!["name"]?.ToString());
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

        var (dataContext, nodeContext, next) = PrepareTest(config, null);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        VerifySetNotCalled(dataContext);
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

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["value"] = "not a number", ["name"] = "Item 1" },
                new JsonObject { ["value"] = true, ["name"] = "Item 2" })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        VerifySetNotCalled(dataContext);
    }

    [Fact]
    public void DefaultMode_IsMin()
    {
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "value"
        };

        Assert.Equal(MinMaxMode.Min, config.Mode);
    }

    [Fact]
    public async Task ProcessObjectAsync_ValuePathWithIndex_ResolvesValue()
    {
        // Capability gain (#7): the bespoke ResolveSubPath walker silently
        // returned null on bracketed paths. With JsonNodePath-backed GetValue the
        // ValuePath "$.readings[0].value" must resolve correctly so that
        // each item's nested-array reading is read for the Min/Max compare.
        var config = new MinMaxNodeConfiguration
        {
            Path = "$.items",
            TargetPath = "$.result",
            ValuePath = "$.readings[0].value",
            Mode = MinMaxMode.Min
        };

        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Item 1",
                    ["readings"] = new JsonArray(new JsonObject { ["value"] = 30 })
                },
                new JsonObject
                {
                    ["name"] = "Item 2",
                    ["readings"] = new JsonArray(new JsonObject { ["value"] = 10 })
                },
                new JsonObject
                {
                    ["name"] = "Item 3",
                    ["readings"] = new JsonArray(new JsonObject { ["value"] = 20 })
                })
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        JsonNode? capturedResult = null;
        CaptureSet(dataContext, config, r => capturedResult = r);

        var node = new MinMaxNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        Assert.NotNull(capturedResult);
        Assert.Equal("Item 2", capturedResult!["name"]?.ToString());
    }
}

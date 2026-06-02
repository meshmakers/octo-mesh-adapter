using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MachineLearningAnomalyNodeTests
{
    private static (IDataContext, INodeContext, IMeshEtlContext) PrepareTest(
        MachineLearningAnomalyNodeConfiguration config,
        JsonNode? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var meshEtlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => meshEtlContext.Properties).Returns(new Dictionary<string, object?>());

        IDataContext dataContext = testData is null
            ? new DataContextImpl(JsonDocument.Parse("{}"))
            : new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext =
            rootNodeContext.RegisterChildNode("MachineLearningAnomalyDetection", 0, config, dataContext);

        return (dataContext, nodeContext, meshEtlContext);
    }

    private static JsonObject CreateTestData(params float[] values)
    {
        var data = new JsonObject
        {
            ["measurements"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["value"] = v }).ToArray())
        };
        return data;
    }

    private static JsonObject CreateGroupedTestData()
    {
        var data = new JsonObject
        {
            ["sensors"] = new JsonArray(
                new JsonObject { ["group"] = "A", ["value"] = 10.0f },
                new JsonObject { ["group"] = "A", ["value"] = 12.0f },
                new JsonObject { ["group"] = "B", ["value"] = 50.0f },
                new JsonObject { ["group"] = "B", ["value"] = 52.0f })
        };
        return data;
    }

    [Fact]
    public async Task ProcessObjectAsync_SpikeDetection_InsufficientData_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 10,
                    SpikeConfidence = 95
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) =
            PrepareTest(config, CreateTestData(10.0f, 11.0f, 12.0f));

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_SpikeDetection_SufficientData_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 5,
                    SpikeConfidence = 95,
                    PValueHistoryLength = 5
                }
            ]
        };

        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 10.2f, 10.0f, 15.0f, 10.1f, 9.8f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_ChangePointDetection_SufficientData_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = false,
                    DetectChangePoints = true,
                    MinDataPoints = 5,
                    ChangePointConfidence = 95,
                    ChangeHistoryLength = 5
                }
            ]
        };

        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 10.2f, 15.0f, 15.1f, 14.9f, 15.2f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_BothDetections_SufficientData_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = true,
                    MinDataPoints = 5,
                    SpikeConfidence = 95,
                    ChangePointConfidence = 95,
                    PValueHistoryLength = 5,
                    ChangeHistoryLength = 5
                }
            ]
        };

        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 25.0f, 15.0f, 15.1f, 14.9f, 15.2f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_GroupedDetection_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.sensors[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    GroupByPath = "group",
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 1,
                    SpikeConfidence = 95
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateGroupedTestData());

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithContextPath_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    ContextPath = "timestamp",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 3,
                    SpikeConfidence = 95
                }
            ]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = 10.0f, ["timestamp"] = "2023-01-01" },
                new JsonObject { ["value"] = 11.0f, ["timestamp"] = "2023-01-02" },
                new JsonObject { ["value"] = 12.0f, ["timestamp"] = "2023-01-03" },
                new JsonObject { ["value"] = 25.0f, ["timestamp"] = "2023-01-04" })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_ResetStatistics_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            ResetStatistics = true,
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 1,
                    SpikeConfidence = 95
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0f));

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task ProcessObjectAsync_MaxDataPointsWindow_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 2,
                    MaxDataPoints = 5,
                    SpikeConfidence = 95
                }
            ]
        };

        var testData = CreateTestData(10.0f, 11.0f, 12.0f, 13.0f, 14.0f, 15.0f, 16.0f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_MultipleDetectors_OK()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 3,
                    SpikeConfidence = 95
                },
                new()
                {
                    Path = "value",
                    DetectSpikes = false,
                    DetectChangePoints = true,
                    MinDataPoints = 3,
                    ChangePointConfidence = 95
                }
            ]
        };

        var testData = CreateTestData(10.0f, 11.0f, 12.0f, 25.0f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_NullInput_ThrowsException()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", DetectSpikes = true }]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, null);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidPath_ThrowsException()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.nonexistent[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", DetectSpikes = true }]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0f));

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidValueFormat_ThrowsException()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", DetectSpikes = true }]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = "not_a_number" })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyData_ThrowsException()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", DetectSpikes = true }]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray()
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_DetectorPathWithIndex_ResolvesValue()
    {
        // Capability gain (#7): with the bespoke ResolveSubPath walker the
        // detector path "$.readings[0].value" silently returned null and the
        // node threw InputValueNull. JsonNodePath.Select must now resolve
        // bracketed sub-paths so the detector reads the nested numeric value.
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "$.readings[0].value",
                    DetectSpikes = true,
                    DetectChangePoints = false,
                    MinDataPoints = 5,
                    SpikeConfidence = 95,
                    PValueHistoryLength = 5
                }
            ]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 10.0f }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 10.1f }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 9.9f }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 10.2f }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 10.0f }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 15.0f }) })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_NullValueInPath_ThrowsException()
    {
        var config = new MachineLearningAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "nonexistent", DetectSpikes = true }]
        };

        var testData = CreateTestData(10.0f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    /// <summary>
    /// Characterization: the typed Spike/ChangePoint anomaly records serialize byte-identically
    /// to the former incrementally-built JsonObjects (fixed "type", detector fields, then the
    /// appended seriesKey/currentValue/context). Covers the with-context and without-context
    /// cases (the "context" key must be omitted when null). ML output is non-deterministic, so
    /// the record shape is pinned directly rather than via a full node run.
    /// </summary>
    [Fact]
    public void SpikeAnomalyResult_SerializesByteIdenticalToLegacyJsonObject()
    {
        var ts = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // With context.
        var withCtx = new MachineLearningAnomalyNode.SpikeAnomalyResult(
            Confidence: 95.0, Level: 1.0, Score: 0.5, PValue: 0.01, Timestamp: ts,
            SeriesKey: "A", CurrentValue: 25.0f, Context: "2023-01-04");
        Assert.Equal(LegacySpike(95.0, 1.0, 0.5, 0.01, ts, "A", 25.0f, "2023-01-04"),
            JsonSerializer.Serialize(withCtx, SystemTextJsonOptions.Default));

        // Without context (key omitted).
        var noCtx = new MachineLearningAnomalyNode.SpikeAnomalyResult(
            Confidence: 95.0, Level: 1.0, Score: 0.5, PValue: 0.01, Timestamp: ts,
            SeriesKey: "", CurrentValue: 25.0f, Context: null);
        var noCtxJson = JsonSerializer.Serialize(noCtx, SystemTextJsonOptions.Default);
        Assert.Equal(LegacySpike(95.0, 1.0, 0.5, 0.01, ts, "", 25.0f, null), noCtxJson);
        Assert.DoesNotContain("\"context\"", noCtxJson);
    }

    [Fact]
    public void ChangePointAnomalyResult_SerializesByteIdenticalToLegacyJsonObject()
    {
        var ts = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        var cp = new MachineLearningAnomalyNode.ChangePointAnomalyResult(
            Confidence: 95.0, Level: 1.0, Score: 0.5, PValue: 0.01, MartingaleValue: 2.5, Timestamp: ts,
            SeriesKey: "B", CurrentValue: 15.0f, Context: "ctx");
        Assert.Equal(LegacyChangePoint(95.0, 1.0, 0.5, 0.01, 2.5, ts, "B", 15.0f, "ctx"),
            JsonSerializer.Serialize(cp, SystemTextJsonOptions.Default));
    }

    // The legacy fixtures build a Dictionary and serialize via JsonSerializer so the
    // NewtonsoftParityDouble/Single converters apply symmetrically with the typed-record path
    // — JsonNode.ToJsonString bypasses the converter chain, which would mask the converter's
    // whole-number ".0" emission and make the byte-equality assertion compare apples to oranges.
    private static string LegacySpike(double confidence, double level, double score, double pValue,
        DateTime timestamp, string seriesKey, float currentValue, object? context)
    {
        var o = new Dictionary<string, object?>
        {
            ["type"] = "spike",
            ["confidence"] = confidence,
            ["level"] = level,
            ["score"] = score,
            ["pValue"] = pValue,
            ["timestamp"] = timestamp,
            ["seriesKey"] = seriesKey,
            ["currentValue"] = currentValue
        };
        if (context != null) o["context"] = context;
        return JsonSerializer.Serialize(o, SystemTextJsonOptions.Default);
    }

    private static string LegacyChangePoint(double confidence, double level, double score, double pValue,
        double martingaleValue, DateTime timestamp, string seriesKey, float currentValue, object? context)
    {
        var o = new Dictionary<string, object?>
        {
            ["type"] = "changePoint",
            ["confidence"] = confidence,
            ["level"] = level,
            ["score"] = score,
            ["pValue"] = pValue,
            ["martingaleValue"] = martingaleValue,
            ["timestamp"] = timestamp,
            ["seriesKey"] = seriesKey,
            ["currentValue"] = currentValue
        };
        if (context != null) o["context"] = context;
        return JsonSerializer.Serialize(o, SystemTextJsonOptions.Default);
    }
}

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

public class StatisticalAnomalyNodeTests
{
    private static (IDataContext, INodeContext, IMeshEtlContext) PrepareTest(
        StatisticalAnomalyNodeConfiguration config,
        JsonNode? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var meshEtlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => meshEtlContext.Properties).Returns(new Dictionary<string, object?>());

        // Use a real DataContextImpl so SelectMatches works against the test data.
        IDataContext dataContext = testData is null
            ? new DataContextImpl(JsonDocument.Parse("{}"))
            : new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("AnomalyDetection", 0, config, dataContext);

        return (dataContext, nodeContext, meshEtlContext);
    }

    private static JsonObject CreateTestData(params double[] values)
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
                new JsonObject { ["group"] = "A", ["value"] = 10.0 },
                new JsonObject { ["group"] = "A", ["value"] = 12.0 },
                new JsonObject { ["group"] = "B", ["value"] = 50.0 },
                new JsonObject { ["group"] = "B", ["value"] = 52.0 })
        };
        return data;
    }

    [Fact]
    public async Task ProcessObjectAsync_ZScoreDetection_NoAnomalies_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 2.0,
                    MinSamples = 3
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) =
            PrepareTest(config, CreateTestData(10.0, 11.0, 12.0, 11.5, 10.5));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_ZScoreDetection_WithAnomalies_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 2.0,
                    MinSamples = 3
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0, 11.0, 12.0, 100.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_IqrDetection_WithAnomalies_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.Iqr,
                    Threshold = 1.5,
                    MinSamples = 5
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) =
            PrepareTest(config, CreateTestData(1.0, 2.0, 3.0, 4.0, 5.0, 100.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_PercentChangeDetection_WithAnomalies_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.PercentChange,
                    Threshold = 50.0,
                    MinSamples = 1
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0, 20.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_MovingAverageDetection_WithAnomalies_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.MovingAverage,
                    Threshold = 50.0,
                    WindowSize = 3,
                    MinSamples = 3
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0, 11.0, 12.0, 50.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_GroupedDetection_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.sensors[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    GroupByPath = "group",
                    Path = "value",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 1.0,
                    MinSamples = 1
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateGroupedTestData());

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithContextPath_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    ContextPath = "timestamp",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 2.0,
                    MinSamples = 3
                }
            ]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = 10.0, ["timestamp"] = "2023-01-01" },
                new JsonObject { ["value"] = 11.0, ["timestamp"] = "2023-01-02" },
                new JsonObject { ["value"] = 12.0, ["timestamp"] = "2023-01-03" },
                new JsonObject { ["value"] = 100.0, ["timestamp"] = "2023-01-04" })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_ResetStatistics_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            ResetStatistics = true,
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 2.0,
                    MinSamples = 1
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task ProcessObjectAsync_NullInput_ThrowsException()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", Method = AnomalyDetectionMethod.ZScore }]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, null);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidPath_ThrowsException()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.nonexistent[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", Method = AnomalyDetectionMethod.ZScore }]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidValueFormat_ThrowsException()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors = [new() { Path = "value", Method = AnomalyDetectionMethod.ZScore }]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = "not_a_number" })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

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
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "$.readings[0].value",
                    Method = AnomalyDetectionMethod.PercentChange,
                    Threshold = 50.0,
                    MinSamples = 1
                }
            ]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 10.0 }) },
                new JsonObject { ["readings"] = new JsonArray(new JsonObject { ["value"] = 20.0 }) })
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_MultipleDetectors_OK()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.ZScore,
                    Threshold = 2.0,
                    MinSamples = 3
                },

                new()
                {
                    Path = "value",
                    Method = AnomalyDetectionMethod.PercentChange,
                    Threshold = 50.0,
                    MinSamples = 1
                }
            ]
        };

        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, CreateTestData(10.0, 11.0, 12.0, 100.0));

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.Equal(DataKind.Array, dataContext.GetKind(config.TargetPath));
    }

    /// <summary>
    /// Characterization: the typed StatisticalAnomalyResult written to TargetPath must serialize
    /// byte-identically to the former hand-built JsonObject (key names/order, double value, float
    /// score, bool isAnomaly), including the optional "context" key. PercentChange over [10, 10, 100]
    /// is deterministic — the third value's 900% change (&gt; 50% threshold) yields one anomaly.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_AnomalyResult_SerializesByteIdenticalToLegacy()
    {
        var config = new StatisticalAnomalyNodeConfiguration
        {
            Path = "$.measurements[*]",
            TargetPath = "$.anomalies",
            Detectors =
            [
                new()
                {
                    Path = "value",
                    ContextPath = "timestamp",
                    Method = AnomalyDetectionMethod.PercentChange,
                    Threshold = 50.0,
                    MinSamples = 1
                }
            ]
        };

        var testData = new JsonObject
        {
            ["measurements"] = new JsonArray(
                new JsonObject { ["value"] = 10.0, ["timestamp"] = "2023-01-01" },
                new JsonObject { ["value"] = 10.0, ["timestamp"] = "2023-01-02" },
                new JsonObject { ["value"] = 100.0, ["timestamp"] = "2023-01-03" })
        };
        var (real, nodeContext, meshEtlContext) = PrepareTest(config, testData);
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        object? captured = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.TargetPath)
            .Invokes(call => captured = call.Arguments[1]);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        var newJson = JsonSerializer.Serialize(captured, captured!.GetType(), SystemTextJsonOptions.Default);

        // Legacy reproduction: one anomaly, third value, 900% change. The reason string is
        // formatted with {change:F2} (current culture) exactly as the node does — reproduce it
        // the same way so the comparison isolates the JsonObject→record change, not formatting.
        // The legacy fixture must serialize via the SAME path as production (JsonSerializer with
        // the configured converters) so the NewtonsoftParityDouble/Single converters apply
        // symmetrically — JsonNode.ToJsonString bypasses the converter chain.
        var reason = $"Change: {900.0:F2}% (threshold: 50%)";
        var legacy = new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = "value",
                ["value"] = 100.0,
                ["isAnomaly"] = true,
                ["score"] = 900f,
                ["method"] = "PercentChange",
                ["reason"] = reason,
                ["context"] = "2023-01-03"
            }
        };

        Assert.Equal(JsonSerializer.Serialize(legacy, SystemTextJsonOptions.Default), newJson);
    }
}

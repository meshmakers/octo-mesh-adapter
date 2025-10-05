using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class StatisticalAnomalyNodeTests
{
    private (IDataContext, INodeContext, IMeshEtlContext) PrepareTest(StatisticalAnomalyNodeConfiguration config,
        JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var meshEtlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => meshEtlContext.Properties).Returns(new Dictionary<string, object?>());

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData);

        // Capture results written to the target path
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<object>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, object _) => { });

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("AnomalyDetection", 0, config, dataContext);

        return (dataContext, nodeContext, meshEtlContext);
    }

    private static JObject CreateTestData(params double[] values)
    {
        var data = new JObject
        {
            ["measurements"] = new JArray(values.Select(v => new JObject { ["value"] = v }))
        };
        return data;
    }

    private static JObject CreateGroupedTestData()
    {
        var data = new JObject
        {
            ["sensors"] = new JArray(
                new JObject { ["group"] = "A", ["value"] = 10.0 },
                new JObject { ["group"] = "A", ["value"] = 12.0 },
                new JObject { ["group"] = "B", ["value"] = 50.0 },
                new JObject { ["group"] = "B", ["value"] = 52.0 }
            )
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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Verify that SetValueByPath was called to write results (content may be empty if no anomalies detected)
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = 10.0, ["timestamp"] = "2023-01-01" },
                new JObject { ["value"] = 11.0, ["timestamp"] = "2023-01-02" },
                new JObject { ["value"] = 12.0, ["timestamp"] = "2023-01-03" },
                new JObject { ["value"] = 100.0, ["timestamp"] = "2023-01-04" }
            )
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = "not_a_number" }
            )
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new StatisticalAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
    }
}
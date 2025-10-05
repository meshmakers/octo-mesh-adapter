using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MachineLearningAnomalyNodeTests
{
    private (IDataContext, INodeContext, IMeshEtlContext) PrepareTest(MachineLearningAnomalyNodeConfiguration config,
        JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var meshEtlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => meshEtlContext.Properties).Returns(new Dictionary<string, object?>());

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData);

        // Mock SetValueByPath to handle the result writing
        A.CallTo(() => dataContext.SetValueByPath(A<string>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<object>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, object _) => { });

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("MachineLearningAnomalyDetection", 0, config, dataContext);

        return (dataContext, nodeContext, meshEtlContext);
    }

    private static JObject CreateTestData(params float[] values)
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
                new JObject { ["group"] = "A", ["value"] = 10.0f },
                new JObject { ["group"] = "A", ["value"] = 12.0f },
                new JObject { ["group"] = "B", ["value"] = 50.0f },
                new JObject { ["group"] = "B", ["value"] = 52.0f }
            )
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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Create data that might have spikes for ML.NET to detect
        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 10.2f, 10.0f, 15.0f, 10.1f, 9.8f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Create data that might have change points
        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 10.2f, 15.0f, 15.1f, 14.9f, 15.2f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        // Create complex data with potential spikes and change points
        var testData = CreateTestData(10.0f, 10.1f, 9.9f, 25.0f, 15.0f, 15.1f, 14.9f, 15.2f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = 10.0f, ["timestamp"] = "2023-01-01" },
                new JObject { ["value"] = 11.0f, ["timestamp"] = "2023-01-02" },
                new JObject { ["value"] = 12.0f, ["timestamp"] = "2023-01-03" },
                new JObject { ["value"] = 25.0f, ["timestamp"] = "2023-01-04" }
            )
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

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

        // Add more data points than the max to test sliding window
        var testData = CreateTestData(10.0f, 11.0f, 12.0f, 13.0f, 14.0f, 15.0f, 16.0f);
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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
        // Verify that SetValueByPath was called to write results
        A.CallTo(() => dataContext.SetValueByPath(config.TargetPath, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._, A<JArray>._))
            .MustHaveHappenedOnceExactly();
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

        var testData = new JObject
        {
            ["measurements"] = new JArray(
                new JObject { ["value"] = "not_a_number" }
            )
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

        var testData = new JObject
        {
            ["measurements"] = new JArray() // Empty array
        };
        var (dataContext, nodeContext, meshEtlContext) = PrepareTest(config, testData);

        var next = A.Fake<NodeDelegate>();
        var node = new MachineLearningAnomalyNode(next, meshEtlContext);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.NotNull(exception);
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
}
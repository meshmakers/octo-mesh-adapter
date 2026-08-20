using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MassTransit.Internals;
using Microsoft.ML;
using Microsoft.ML.Data;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Time series anomaly detection node using ML.NET for spike and change point detection
/// </summary>
[NodeConfiguration(typeof(MachineLearningAnomalyNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class MachineLearningAnomalyNode(NodeDelegate next, IMeshEtlContext meshEtlContext) : IPipelineNode
{
    private readonly MLContext _mlContext = new(seed: 0);
    private const string AnomalyDetectionStatistics = "MachineLearningAnomalyDetection.TimeSeriesData";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<MachineLearningAnomalyNodeConfiguration>();

        // Get or create statistics dictionary in context
        if (!meshEtlContext.Properties.TryGetValue(AnomalyDetectionStatistics, out var obj) ||
            obj is not Dictionary<string, TimeSeriesData> timeSeriesDataMap || c.ResetStatistics)
        {
            timeSeriesDataMap = new Dictionary<string, TimeSeriesData>();
            meshEtlContext.Properties[AnomalyDetectionStatistics] = timeSeriesDataMap;
        }

        // Use multi-match JSONPath read so wildcards (e.g. $.items[*]) and recursive
        // descent (e.g. $..value) yield every matching element. This restores the legacy
        // SelectTokens(path) semantics from the Newtonsoft implementation.
        var sourceData = dataContext.SelectMatches(c.Path).ToArray();
        if (sourceData.Length == 0)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, c.Path);
        }

        var results = new List<object>();

        foreach (var detector in c.Detectors)
        {
            if (!string.IsNullOrWhiteSpace(detector.GroupByPath))
            {
                var groupBy = sourceData.GroupBy(x =>
                    AnomalyNodeHelpers.GetPropertyAsString(x.Get<JsonNode>("$"), detector.GroupByPath));
                foreach (var group in groupBy)
                {
                    Calculate(nodeContext, timeSeriesDataMap, group.Key ?? "", group.ToArray(), detector, results);
                }
            }
            else
            {
                Calculate(nodeContext, timeSeriesDataMap, "", sourceData, detector, results);
            }
        }


        // Set results
        dataContext.Set(c.TargetPath, results, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }

    private void Calculate(INodeContext nodeContext, Dictionary<string, TimeSeriesData> timeSeriesDataMap, string key,
        IDataContext[] sourceData, MachineLearningAnomalyDetectorConfiguration detector, List<object> results)
    {
        if (!sourceData.Any())
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var valuePath = JsonNodePath.NormalizePathOrRelative(detector.Path);

        foreach (var sourceDataItem in sourceData)
        {
            var sourceToken = sourceDataItem.Get<JsonNode>(valuePath);
            if (sourceToken == null)
            {
                throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, detector.Path);
            }

            if (!JsonScalar.TryToNumber<float>(sourceToken, out var value))
            {
                throw MeshAdapterPipelineExecutionException.InputValueInvalidFormat(
                    nodeContext, detector.Path, new FormatException("Cannot read as float"));
            }

            // Get or create time series data
            var series = timeSeriesDataMap.GetOrAdd(key, _ => new TimeSeriesData());
            series.Add(value);

            // Need minimum data points for detection
            if (series.Values.Count < detector.MinDataPoints)
            {
                nodeContext.Debug("Insufficient data points ({0}/{1}) for time series analysis",
                    series.Values.Count, detector.MinDataPoints);
                continue;
            }

            // Resolve context once per item (dynamic — scalars to CLR, objects/arrays to
            // JsonElement, re-serializing byte-identically). Null => "context" key omitted;
            // configured-but-missing => "" exactly as the legacy did. The records carry
            // seriesKey / currentValue / context as their trailing members so the serialized
            // key order matches the former incremental JsonObject mutation.
            object? context = null;
            if (!string.IsNullOrWhiteSpace(detector.ContextPath))
            {
                var contextPath = JsonNodePath.NormalizePathOrRelative(detector.ContextPath);
                context = sourceDataItem.Get<object?>(contextPath) ?? "";
            }

            if (detector.DetectSpikes)
            {
                var spike = DetectSpike(series, detector, nodeContext, key, value, context);
                if (spike != null) results.Add(spike);
            }

            if (detector.DetectChangePoints)
            {
                var changePoint = DetectChangePoint(series, detector, nodeContext, key, value, context);
                if (changePoint != null) results.Add(changePoint);
            }

            // Maintain sliding window
            if (detector.MaxDataPoints > 0 && series.Values.Count > detector.MaxDataPoints)
            {
                series.RemoveOldest(series.Values.Count - detector.MaxDataPoints);
            }
        }
    }

    private SpikeAnomalyResult? DetectSpike(TimeSeriesData series,
        MachineLearningAnomalyDetectorConfiguration detector, INodeContext nodeContext,
        string key, float currentValue, object? context)
    {
        try
        {
            var dataView = _mlContext.Data.LoadFromEnumerable(
                series.Values.Select(v => new TimeSeriesDataPoint { Value = v })
            );

            var pipeline = _mlContext.Transforms.DetectIidSpike(
                outputColumnName: nameof(SpikePrediction.Prediction),
                inputColumnName: nameof(TimeSeriesDataPoint.Value),
                confidence: detector.SpikeConfidence,
                pvalueHistoryLength: detector.PValueHistoryLength);

            var model = pipeline.Fit(dataView);
            var transformedData = model.Transform(dataView);
            var predictions = _mlContext.Data.CreateEnumerable<SpikePrediction>(
                transformedData, reuseRowObject: false).ToList();

            var lastPrediction = predictions.LastOrDefault();
            if (lastPrediction?.Prediction[0] > 0)
            {
                nodeContext.Debug("Spike detected with score {0}", lastPrediction.Prediction[1]);
                return new SpikeAnomalyResult(
                    detector.SpikeConfidence,
                    lastPrediction.Prediction[0],
                    lastPrediction.Prediction[1],
                    lastPrediction.Prediction[2],
                    DateTime.UtcNow,
                    key,
                    currentValue,
                    context);
            }
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.SpikeDetectionFailed(nodeContext, ex);
        }

        return null;
    }

    private ChangePointAnomalyResult? DetectChangePoint(TimeSeriesData series,
        MachineLearningAnomalyDetectorConfiguration detector, INodeContext nodeContext,
        string key, float currentValue, object? context)
    {
        try
        {
            var dataView = _mlContext.Data.LoadFromEnumerable(
                series.Values.Select(v => new TimeSeriesDataPoint { Value = v })
            );

            var pipeline = _mlContext.Transforms.DetectIidChangePoint(
                outputColumnName: nameof(ChangePointPrediction.Prediction),
                inputColumnName: nameof(TimeSeriesDataPoint.Value),
                confidence: detector.ChangePointConfidence,
                changeHistoryLength: detector.ChangeHistoryLength);

            var model = pipeline.Fit(dataView);
            var transformedData = model.Transform(dataView);
            var predictions = _mlContext.Data.CreateEnumerable<ChangePointPrediction>(
                transformedData, reuseRowObject: false).ToList();

            var lastPrediction = predictions.LastOrDefault();
            if (lastPrediction?.Prediction[0] > 0)
            {
                nodeContext.Debug("Change point detected with score {0}", lastPrediction.Prediction[1]);
                return new ChangePointAnomalyResult(
                    detector.ChangePointConfidence,
                    lastPrediction.Prediction[0],
                    lastPrediction.Prediction[1],
                    lastPrediction.Prediction[2],
                    lastPrediction.Prediction[3],
                    DateTime.UtcNow,
                    key,
                    currentValue,
                    context);
            }
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.ChangePointDetectionFailed(nodeContext, ex);
        }

        return null;
    }

    /// <summary>
    /// Typed shape of a spike anomaly. Member order/names reproduce the former JsonObject
    /// (a fixed "type" discriminator, the detector fields, then the appended
    /// seriesKey/currentValue/context). "context" is optional (omitted when no ContextPath
    /// is configured). Explicit JsonPropertyOrder pins the key order regardless of how STJ
    /// interleaves the computed Type property with the positional parameters.
    /// </summary>
    internal sealed record SpikeAnomalyResult(
        [property: JsonPropertyName("confidence")] [property: JsonPropertyOrder(1)] double Confidence,
        [property: JsonPropertyName("level")] [property: JsonPropertyOrder(2)] double Level,
        [property: JsonPropertyName("score")] [property: JsonPropertyOrder(3)] double Score,
        [property: JsonPropertyName("pValue")] [property: JsonPropertyOrder(4)] double PValue,
        [property: JsonPropertyName("timestamp")] [property: JsonPropertyOrder(5)] DateTime Timestamp,
        [property: JsonPropertyName("seriesKey")] [property: JsonPropertyOrder(6)] string SeriesKey,
        [property: JsonPropertyName("currentValue")] [property: JsonPropertyOrder(7)] float CurrentValue,
        [property: JsonPropertyName("context")] [property: JsonPropertyOrder(8)]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? Context)
    {
        [JsonPropertyName("type")]
        [JsonPropertyOrder(0)]
        public string Type => "spike";
    }

    /// <summary>
    /// Typed shape of a change-point anomaly. Same as the spike shape plus "martingaleValue"
    /// inserted between pValue and timestamp, matching the former JsonObject key order.
    /// </summary>
    internal sealed record ChangePointAnomalyResult(
        [property: JsonPropertyName("confidence")] [property: JsonPropertyOrder(1)] double Confidence,
        [property: JsonPropertyName("level")] [property: JsonPropertyOrder(2)] double Level,
        [property: JsonPropertyName("score")] [property: JsonPropertyOrder(3)] double Score,
        [property: JsonPropertyName("pValue")] [property: JsonPropertyOrder(4)] double PValue,
        [property: JsonPropertyName("martingaleValue")] [property: JsonPropertyOrder(5)] double MartingaleValue,
        [property: JsonPropertyName("timestamp")] [property: JsonPropertyOrder(6)] DateTime Timestamp,
        [property: JsonPropertyName("seriesKey")] [property: JsonPropertyOrder(7)] string SeriesKey,
        [property: JsonPropertyName("currentValue")] [property: JsonPropertyOrder(8)] float CurrentValue,
        [property: JsonPropertyName("context")] [property: JsonPropertyOrder(9)]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? Context)
    {
        [JsonPropertyName("type")]
        [JsonPropertyOrder(0)]
        public string Type => "changePoint";
    }

    private class TimeSeriesData
    {
        public List<float> Values { get; } = new();

        public void Add(float value)
        {
            Values.Add(value);
        }

        public void RemoveOldest(int count)
        {
            if (count > 0 && count < Values.Count)
            {
                Values.RemoveRange(0, count);
            }
        }
    }

    private class TimeSeriesDataPoint
    {
        public float Value { get; set; }
    }

    private class SpikePrediction
    {
        [VectorType(3)] public double[] Prediction { get; set; } = new double[3];
    }

    private class ChangePointPrediction
    {
        [VectorType(4)] public double[] Prediction { get; set; } = new double[4];
    }
}
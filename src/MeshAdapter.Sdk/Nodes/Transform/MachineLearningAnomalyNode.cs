using MassTransit.Internals;
using Microsoft.ML;
using Microsoft.ML.Data;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Newtonsoft.Json.Linq;

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

        if (dataContext.Current == null)
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var sourceData = dataContext.Current.SelectTokens(c.Path).ToArray();
        if (sourceData == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, c.Path);
        }

        var results = new JArray();

        foreach (var detector in c.Detectors)
        {
            if (!string.IsNullOrWhiteSpace(detector.GroupByPath))
            {
                var groupBy = sourceData.GroupBy(x => x.SelectToken(detector.GroupByPath));
                foreach (var group in groupBy)
                {
                    var groupToken = group.Key?.Value<string>() ?? "";
                    Calculate(nodeContext, timeSeriesDataMap, groupToken, group.ToArray(), detector, results);
                }
            }
            else
            {
                Calculate(nodeContext, timeSeriesDataMap, "", sourceData, detector, results);
            }
        }


        // Set results
        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, results);

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }

    private void Calculate(INodeContext nodeContext,Dictionary<string, TimeSeriesData> timeSeriesDataMap, string key,
        JToken[] sourceData, MachineLearningAnomalyDetectorConfiguration detector, JArray results)
    {
        if (!sourceData.Any())
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        foreach (var sourceDataItem in sourceData)
        {
            var sourceToken = sourceDataItem.SelectToken(detector.Path);
            if (sourceToken == null)
            {
                throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, detector.Path);
            }

            float value;
            try
            {
                value = sourceToken.ToObject<float>();
            }
            catch (FormatException e)
            {
                throw MeshAdapterPipelineExecutionException.InputValueInvalidFormat(nodeContext, detector.Path, e);
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

            // Detect anomalies
            var anomalies = new List<JObject>();

            if (detector.DetectSpikes)
            {
                var spikes = DetectSpikes(series, detector, nodeContext);
                anomalies.AddRange(spikes);
            }

            if (detector.DetectChangePoints)
            {
                var changePoints = DetectChangePoints(series, detector, nodeContext);
                anomalies.AddRange(changePoints);
            }

            // Add anomalies to results
            foreach (var anomaly in anomalies)
            {
                anomaly["seriesKey"] = key;
                anomaly["currentValue"] = value;

                // Add context if specified
                if (!string.IsNullOrWhiteSpace(detector.ContextPath))
                {
                    var contextValue = sourceDataItem.GetSimpleValueByPath<object>(detector.ContextPath);
                    anomaly["context"] = JToken.FromObject(contextValue ?? "");
                }

                results.Add(anomaly);
            }

            // Maintain sliding window
            if (detector.MaxDataPoints > 0 && series.Values.Count > detector.MaxDataPoints)
            {
                series.RemoveOldest(series.Values.Count - detector.MaxDataPoints);
            }
        }
    }

    private List<JObject> DetectSpikes(TimeSeriesData series, MachineLearningAnomalyDetectorConfiguration detector,
        INodeContext nodeContext)
    {
        var results = new List<JObject>();

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
                results.Add(new JObject
                {
                    ["type"] = "spike",
                    ["confidence"] = detector.SpikeConfidence,
                    ["level"] = lastPrediction.Prediction[0],
                    ["score"] = lastPrediction.Prediction[1],
                    ["pValue"] = lastPrediction.Prediction[2],
                    ["timestamp"] = DateTime.UtcNow
                });

                nodeContext.Debug("Spike detected with score {0}", lastPrediction.Prediction[1]);
            }
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.SpikeDetectionFailed(nodeContext, ex);
        }

        return results;
    }

    private List<JObject> DetectChangePoints(TimeSeriesData series, MachineLearningAnomalyDetectorConfiguration detector,
        INodeContext nodeContext)
    {
        var results = new List<JObject>();

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
                results.Add(new JObject
                {
                    ["type"] = "changePoint",
                    ["confidence"] = detector.ChangePointConfidence,
                    ["level"] = lastPrediction.Prediction[0],
                    ["score"] = lastPrediction.Prediction[1],
                    ["pValue"] = lastPrediction.Prediction[2],
                    ["martingaleValue"] = lastPrediction.Prediction[3],
                    ["timestamp"] = DateTime.UtcNow
                });

                nodeContext.Debug("Change point detected with score {0}", lastPrediction.Prediction[1]);
            }
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.ChangePointDetectionFailed(nodeContext, ex);
        }

        return results;
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
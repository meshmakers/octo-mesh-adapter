using MassTransit.Internals;
using Microsoft.ML;
using Microsoft.ML.Data;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Time series anomaly detection node using ML.NET for spike and change point detection
/// </summary>
[NodeConfiguration(typeof(TimeSeriesAnomalyDetectionNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class TimeSeriesAnomalyDetectionNode(NodeDelegate next, IMeshEtlContext meshEtlContext) : IPipelineNode
{
    private readonly MLContext _mlContext = new(seed: 0);
    private const string AnomalyDetectionStatistics = "TimeSeriesAnomalyDetection.TimeSeriesData";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<TimeSeriesAnomalyDetectionNodeConfiguration>();

        // Get or create statistics dictionary in context
        if (!meshEtlContext.Properties.TryGetValue(AnomalyDetectionStatistics, out var obj) ||
            obj is not Dictionary<string, TimeSeriesData> timeSeriesData)
        {
            timeSeriesData = new Dictionary<string, TimeSeriesData>();
            meshEtlContext.Properties[AnomalyDetectionStatistics] = timeSeriesData;
        }
        
        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        var results = new JArray();
        
        // Extract value from path
        var sourceTokens = dataContext.Current.SelectTokens(c.ValuePath).ToArray();
        
        if (!sourceTokens.Any())
        {
            nodeContext.Warning("No data found at path '{0}'", c.ValuePath);
            await next(dataContext, nodeContext).ConfigureAwait(false);
            return;
        }

        foreach (var sourceToken in sourceTokens)
        {
            var value = sourceToken.ToObject<float?>();
            if (value == null)
            {
                nodeContext.Warning("No numeric value found at path '{0}'", c.ValuePath);
                continue;
            }

            // Get or create time series data
            var key = c.SeriesKey ?? c.ValuePath;
            var series = timeSeriesData.GetOrAdd(key, _ => new TimeSeriesData());
            series.Add(value.Value);

            // Need minimum data points for detection
            if (series.Values.Count < c.MinDataPoints)
            {
                nodeContext.Debug("Insufficient data points ({0}/{1}) for time series analysis", 
                    series.Values.Count, c.MinDataPoints);
                await next(dataContext, nodeContext).ConfigureAwait(false);
                return;
            }

            // Detect anomalies
            var anomalies = new List<JObject>();
            
            if (c.DetectSpikes)
            {
                var spikes = DetectSpikes(series, c, nodeContext);
                anomalies.AddRange(spikes);
            }
            
            if (c.DetectChangePoints)
            {
                var changePoints = DetectChangePoints(series, c, nodeContext);
                anomalies.AddRange(changePoints);
            }

            // Add anomalies to results
            foreach (var anomaly in anomalies)
            {
                anomaly["seriesKey"] = key;
                anomaly["currentValue"] = value.Value;
                
                // Add context if specified
                if (!string.IsNullOrWhiteSpace(c.ContextPath))
                {
                    var contextValue = dataContext.GetSimpleValueByPath<object>(c.ContextPath);
                    anomaly["context"] = JToken.FromObject(contextValue ?? "");
                }
                
                results.Add(anomaly);
            }

            // Maintain sliding window
            if (c.MaxDataPoints > 0 && series.Values.Count > c.MaxDataPoints)
            {
                series.RemoveOldest(series.Values.Count - c.MaxDataPoints);
            }
        }
        
        // Set results
        if (results.Any() || c.AlwaysSetResults)
        {
            dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, 
                c.TargetValueWriteMode, results);
        }
        
        await next(dataContext, nodeContext).ConfigureAwait(false);
    }
    
    private List<JObject> DetectSpikes(TimeSeriesData series, TimeSeriesAnomalyDetectionNodeConfiguration config, INodeContext nodeContext)
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
                confidence: config.SpikeConfidence / 100.0,
                pvalueHistoryLength: config.PValueHistoryLength);
            
            var model = pipeline.Fit(dataView);
            var transformedData = model.Transform(dataView);
            var predictions = _mlContext.Data.CreateEnumerable<SpikePrediction>(
                transformedData, reuseRowObject: false).ToList();
            
            var lastPrediction = predictions.LastOrDefault();
            if (lastPrediction?.Prediction[0] == 1)
            {
                results.Add(new JObject
                {
                    ["type"] = "spike",
                    ["confidence"] = config.SpikeConfidence,
                    ["score"] = lastPrediction.Prediction[1],
                    ["pValue"] = lastPrediction.Prediction[2],
                    ["timestamp"] = DateTime.UtcNow
                });
                
                nodeContext.Debug("Spike detected with score {0}", lastPrediction.Prediction[1]);
            }
        }
        catch (Exception ex)
        {
            nodeContext.Error("Error detecting spikes: {0}", ex.Message);
        }
        
        return results;
    }
    
    private List<JObject> DetectChangePoints(TimeSeriesData series, TimeSeriesAnomalyDetectionNodeConfiguration config, INodeContext nodeContext)
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
                confidence: config.ChangePointConfidence / 100.0,
                changeHistoryLength: config.ChangeHistoryLength);
            
            var model = pipeline.Fit(dataView);
            var transformedData = model.Transform(dataView);
            var predictions = _mlContext.Data.CreateEnumerable<ChangePointPrediction>(
                transformedData, reuseRowObject: false).ToList();
            
            var lastPrediction = predictions.LastOrDefault();
            if (lastPrediction?.Prediction[0] == 1)
            {
                results.Add(new JObject
                {
                    ["type"] = "changePoint",
                    ["confidence"] = config.ChangePointConfidence,
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
            nodeContext.Error("Error detecting change points: {0}", ex.Message);
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
        [VectorType(3)]
        public double[] Prediction { get; set; } = new double[3];
    }
    
    private class ChangePointPrediction  
    {
        [VectorType(4)]
        public double[] Prediction { get; set; } = new double[4];
    }
}

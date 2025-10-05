using MassTransit.Internals;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Anomaly detection node for detecting outliers in numeric data streams.
/// Detects anomalies using various statistical methods such as Z-Score, IQR, Percent Change, and Moving Average.
/// </summary>
[NodeConfiguration(typeof(StatisticalAnomalyNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class StatisticalAnomalyNode(NodeDelegate next, IMeshEtlContext meshEtlContext) : IPipelineNode
{
    private const string AnomalyDetectionStatistics = "AnomalyDetection.Statistics";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<StatisticalAnomalyNodeConfiguration>();

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }


        // Get or create statistics dictionary in context
        if (!meshEtlContext.Properties.TryGetValue(AnomalyDetectionStatistics, out var obj) ||
            obj is not Dictionary<string, RunningStatistics> statisticsMap || c.ResetStatistics)
        {
            statisticsMap = new Dictionary<string, RunningStatistics>();
            meshEtlContext.Properties[AnomalyDetectionStatistics] = statisticsMap;
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
                    Calculate(nodeContext, statisticsMap, groupToken, group.ToArray(), detector, results);
                }
            }
            else
            {
                Calculate(nodeContext, statisticsMap, "", sourceData, detector, results);
            }
        }

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, results);

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }

    private void Calculate(INodeContext nodeContext, Dictionary<string, RunningStatistics> statisticsMap, string key,
        JToken[] sourceData, StatisticalDetectorConfiguration statisticalDetector, JArray results)
    {
        if (!sourceData.Any())
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        foreach (var sourceDataItem in sourceData)
        {
            var sourceToken = sourceDataItem.SelectToken(statisticalDetector.Path);
            if (sourceToken == null)
            {
                throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, statisticalDetector.Path);
            }
            double value;
            try
            {
                value = sourceToken.ToObject<double>();
            }
            catch (FormatException e)
            {
                throw MeshAdapterPipelineExecutionException.InputValueInvalidFormat(nodeContext, statisticalDetector.Path, e);
            }

            var anomalyResult = DetectAnomaly(statisticsMap, key, value, statisticalDetector);

            if (anomalyResult.IsAnomaly)
            {
                var resultObject = new JObject
                {
                    ["path"] = statisticalDetector.Path,
                    ["value"] = value,
                    ["isAnomaly"] = anomalyResult.IsAnomaly,
                    ["score"] = anomalyResult.Score,
                    ["method"] = anomalyResult.Method,
                    ["reason"] = anomalyResult.Reason
                };

                // Add context data if specified
                if (!string.IsNullOrWhiteSpace(statisticalDetector.ContextPath))
                {
                    var contextValue = sourceDataItem.GetSimpleValueByPath<object>(statisticalDetector.ContextPath);
                    resultObject["context"] = JToken.FromObject(contextValue ?? "");
                }

                nodeContext.Debug("Anomaly detected: {0} at path {1} with score {2}",
                    anomalyResult.Reason, statisticalDetector.Path, anomalyResult.Score);

                results.Add(resultObject);
            }
        }
    }

    private AnomalyResult DetectAnomaly(Dictionary<string, RunningStatistics> statisticsMap, string key, double value,
        StatisticalDetectorConfiguration statisticalDetector)
    {
        var stats = statisticsMap.GetOrAdd(key, _ => new RunningStatistics());

        var result = new AnomalyResult { Method = statisticalDetector.Method.ToString() };

        switch (statisticalDetector.Method)
        {
            case AnomalyDetectionMethod.ZScore:
                if (stats.Count > statisticalDetector.MinSamples)
                {
                    var zScore = Math.Abs((value - stats.Mean) / stats.StandardDeviation);
                    result.Score = (float)zScore;
                    result.IsAnomaly = zScore > statisticalDetector.Threshold;
                    result.Reason = $"Z-Score: {zScore:F2} (threshold: {statisticalDetector.Threshold})";
                }

                break;

            case AnomalyDetectionMethod.Iqr:
                if (stats.Count > statisticalDetector.MinSamples)
                {
                    var iqrMultiplier = statisticalDetector.Threshold;
                    var q1 = stats.GetPercentile(25);
                    var q3 = stats.GetPercentile(75);
                    var iqr = q3 - q1;
                    var lowerBound = q1 - iqrMultiplier * iqr;
                    var upperBound = q3 + iqrMultiplier * iqr;

                    result.IsAnomaly = value < lowerBound || value > upperBound;
                    result.Score = result.IsAnomaly
                        ? (float)(Math.Max(Math.Abs(value - lowerBound), Math.Abs(value - upperBound)) / iqr)
                        : 0;
                    result.Reason = $"Value outside IQR bounds [{lowerBound:F2}, {upperBound:F2}]";
                }

                break;

            case AnomalyDetectionMethod.PercentChange:
                if (stats is { Count: > 0, LastValue: not null })
                {
                    var change = Math.Abs((value - stats.LastValue.Value) / stats.LastValue.Value) * 100;
                    result.Score = (float)change;
                    result.IsAnomaly = change > statisticalDetector.Threshold;
                    result.Reason = $"Change: {change:F2}% (threshold: {statisticalDetector.Threshold}%)";
                }

                break;

            case AnomalyDetectionMethod.MovingAverage:
                if (stats.Count >= statisticalDetector.WindowSize)
                {
                    var movingAvg = stats.GetMovingAverage(statisticalDetector.WindowSize);
                    var deviation = Math.Abs((value - movingAvg) / movingAvg) * 100;
                    result.Score = (float)deviation;
                    result.IsAnomaly = deviation > statisticalDetector.Threshold;
                    result.Reason = $"Deviation from MA: {deviation:F2}% (threshold: {statisticalDetector.Threshold}%)";
                }

                break;
        }

        // Update statistics
        stats.Add(value);

        // Clean up old statistics if needed
        if (statisticalDetector.MaxSamples > 0 && stats.Count > statisticalDetector.MaxSamples)
        {
            stats.RemoveOldest();
        }

        return result;
    }

    private class AnomalyResult
    {
        public bool IsAnomaly { get; set; }
        public float Score { get; set; }
        public string Method { get; init; } = "";
        public string Reason { get; set; } = "";
    }

    private class RunningStatistics
    {
        private readonly List<double> _values = new();
        public int Count => _values.Count;
        public double Mean { get; private set; }
        public double StandardDeviation { get; private set; }
        public double? LastValue { get; private set; }

        public void Add(double value)
        {
            LastValue = _values.Count > 0 ? _values.Last() : null;
            _values.Add(value);
            UpdateStats();
        }

        public void RemoveOldest()
        {
            if (_values.Count > 0)
            {
                _values.RemoveAt(0);
                UpdateStats();
            }
        }

        private void UpdateStats()
        {
            if (_values.Count == 0) return;

            Mean = _values.Average();
            var sumOfSquares = _values.Sum(v => Math.Pow(v - Mean, 2));
            StandardDeviation = Math.Sqrt(sumOfSquares / _values.Count);
        }

        public double GetPercentile(int percentile)
        {
            if (_values.Count == 0) return 0;

            var sorted = _values.OrderBy(v => v).ToList();
            var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
        }

        public double GetMovingAverage(int windowSize)
        {
            var start = Math.Max(0, _values.Count - windowSize);
            return _values.Skip(start).Average();
        }
    }
}
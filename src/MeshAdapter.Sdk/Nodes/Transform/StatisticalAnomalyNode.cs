using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MassTransit.Internals;
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

        // Get or create statistics dictionary in context
        if (!meshEtlContext.Properties.TryGetValue(AnomalyDetectionStatistics, out var obj) ||
            obj is not Dictionary<string, RunningStatistics> statisticsMap || c.ResetStatistics)
        {
            statisticsMap = new Dictionary<string, RunningStatistics>();
            meshEtlContext.Properties[AnomalyDetectionStatistics] = statisticsMap;
        }

        // Use multi-match JSONPath read so wildcards (e.g. $.items[*]) and recursive
        // descent (e.g. $..value) yield every matching element. This restores the legacy
        // SelectTokens(path) semantics from the Newtonsoft implementation.
        var sourceData = dataContext.SelectMatches(c.Path).ToArray();
        if (sourceData.Length == 0)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, c.Path);
        }

        var results = new List<StatisticalAnomalyResult>();

        foreach (var detector in c.Detectors)
        {
            if (!string.IsNullOrWhiteSpace(detector.GroupByPath))
            {
                var groupBy = sourceData.GroupBy(x =>
                    AnomalyNodeHelpers.GetPropertyAsString(x.Get<JsonNode>("$"), detector.GroupByPath));
                foreach (var group in groupBy)
                {
                    Calculate(nodeContext, statisticsMap, group.Key ?? "", group.ToArray(), detector, results);
                }
            }
            else
            {
                Calculate(nodeContext, statisticsMap, "", sourceData, detector, results);
            }
        }

        dataContext.Set(c.TargetPath, results, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }

    private void Calculate(INodeContext nodeContext, Dictionary<string, RunningStatistics> statisticsMap, string key,
        IDataContext[] sourceData, StatisticalDetectorConfiguration statisticalDetector,
        List<StatisticalAnomalyResult> results)
    {
        if (!sourceData.Any())
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var valuePath = JsonNodePath.NormalizePathOrRelative(statisticalDetector.Path);

        foreach (var sourceDataItem in sourceData)
        {
            var sourceToken = sourceDataItem.Get<JsonNode>(valuePath);
            if (sourceToken == null)
            {
                throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext, statisticalDetector.Path);
            }
            if (!JsonScalar.TryToNumber<double>(sourceToken, out var value))
            {
                throw MeshAdapterPipelineExecutionException.InputValueInvalidFormat(
                    nodeContext, statisticalDetector.Path, new FormatException("Cannot read as double"));
            }

            var anomalyResult = DetectAnomaly(statisticsMap, key, value, statisticalDetector);

            if (anomalyResult.IsAnomaly)
            {
                // Context is dynamic (any JSON kind); read as object? so scalars materialize
                // to CLR and objects/arrays to JsonElement, re-serializing byte-identically.
                // Null Context omits the "context" key (property-level WhenWritingNull override);
                // a configured-but-missing context falls back to "" exactly as the legacy did.
                object? context = null;
                if (!string.IsNullOrWhiteSpace(statisticalDetector.ContextPath))
                {
                    var contextPath = JsonNodePath.NormalizePathOrRelative(statisticalDetector.ContextPath);
                    context = sourceDataItem.Get<object?>(contextPath) ?? "";
                }

                nodeContext.Debug("Anomaly detected: {0} at path {1} with score {2}",
                    anomalyResult.Reason, statisticalDetector.Path, anomalyResult.Score);

                results.Add(new StatisticalAnomalyResult(
                    statisticalDetector.Path,
                    value,
                    anomalyResult.IsAnomaly,
                    anomalyResult.Score,
                    anomalyResult.Method,
                    anomalyResult.Reason,
                    context));
            }
        }
    }

    /// <summary>
    /// Typed shape of a statistical anomaly result. Key names/order match the former JsonObject;
    /// "context" is optional (omitted when no ContextPath is configured) and dynamic.
    /// </summary>
    internal sealed record StatisticalAnomalyResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("isAnomaly")] bool IsAnomaly,
        [property: JsonPropertyName("score")] float Score,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("context")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? Context);

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
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for time series anomaly detection using ML.NET machine learning
/// </summary>
[NodeName("MachineLearningAnomalyDetection", 1)]
public record MachineLearningAnomalyNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// List of detector configurations for different fields
    /// </summary>
    public List<MachineLearningAnomalyDetectorConfiguration> Detectors { get; set; } = new();

    /// <summary>
    /// Reset statistics on each run (true = stateless, false = stateful)
    /// </summary>
    public bool ResetStatistics { get; set; } = false;
}

/// <summary>
/// Definition of individual time series anomaly detector
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record MachineLearningAnomalyDetectorConfiguration
{
    /// <summary>
    /// JSONPath to group by (empty = no grouping)
    /// </summary>
    public string? GroupByPath { get; set; }

    /// <summary>
    /// JSONPath to the double value to monitor
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Optional path to context data to include with anomaly results
    /// </summary>
    public string? ContextPath { get; set; }

    /// <summary>
    /// Enable spike detection
    /// </summary>
    public bool DetectSpikes { get; set; } = true;

    /// <summary>
    /// Enable change point detection
    /// </summary>
    public bool DetectChangePoints { get; set; } = true;

    /// <summary>
    /// Confidence level for spike detection (0-100)
    /// </summary>
    public double SpikeConfidence { get; set; } = 95;

    /// <summary>
    /// Confidence level for change point detection (0-100)
    /// </summary>
    public double ChangePointConfidence { get; set; } = 95;

    /// <summary>
    /// Size of the sliding window for computing p-value in spike detection
    /// </summary>
    public int PValueHistoryLength { get; set; } = 30;

    /// <summary>
    /// Size of the sliding window for change point detection
    /// </summary>
    public int ChangeHistoryLength { get; set; } = 10;

    /// <summary>
    /// Minimum data points required before detection starts
    /// </summary>
    public int MinDataPoints { get; set; } = 20;

    /// <summary>
    /// Maximum data points to keep in memory (0 = unlimited)
    /// </summary>
    public int MaxDataPoints { get; set; } = 1000;
}
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for time series anomaly detection using ML.NET
/// </summary>
[NodeName("TimeSeriesAnomalyDetection", 1)]
public record TimeSeriesAnomalyDetectionNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// JSONPath to the numeric value to analyze
    /// </summary>
    public string ValuePath { get; set; } = "$.value";
    
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
    public int SpikeConfidence { get; set; } = 95;
    
    /// <summary>
    /// Confidence level for change point detection (0-100)
    /// </summary>
    public int ChangePointConfidence { get; set; } = 95;
    
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
    
    /// <summary>
    /// Optional key for time series storage (defaults to ValuePath if not specified)
    /// </summary>
    public string? SeriesKey { get; set; }
    
    /// <summary>
    /// Always set results array even if no anomalies are detected
    /// </summary>
    public bool AlwaysSetResults { get; set; } = false;
}

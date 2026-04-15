using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for anomaly detection node using statistical methods
/// </summary>
[NodeName("StatisticalAnomalyDetection", 1)]
public record StatisticalAnomalyNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// List of detector configurations for different fields
    /// </summary>
    [PropertyGroup("AI Configuration", 0)]
    public List<StatisticalDetectorConfiguration> Detectors { get; set; } = new();

    /// <summary>
    /// Reset statistics on each run (true = stateless, false = stateful)
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool ResetStatistics { get; set; } = false;
}

/// <summary>
/// Configuration for individual anomaly detector
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record StatisticalDetectorConfiguration
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
    /// Detection method to use
    /// </summary>
    public AnomalyDetectionMethod Method { get; set; } = AnomalyDetectionMethod.ZScore;
    
    /// <summary>
    /// Threshold for anomaly detection (interpretation depends on method)
    /// </summary>
    public double Threshold { get; set; } = 3.0;
    
    /// <summary>
    /// Minimum samples required before detection starts
    /// </summary>
    public int MinSamples { get; set; } = 10;
    
    /// <summary>
    /// Maximum samples to keep in memory (0 = unlimited)
    /// </summary>
    public int MaxSamples { get; set; } = 1000;
    
    /// <summary>
    /// Window size for moving average method
    /// </summary>
    public int WindowSize { get; set; } = 10;
}

/// <summary>
/// Anomaly detection methods
/// </summary>
public enum AnomalyDetectionMethod
{
    /// <summary>
    /// Z-Score based detection (threshold = number of standard deviations)
    /// </summary>
    ZScore,
    
    /// <summary>
    /// Interquartile Range based detection (threshold = IQR multiplier)
    /// </summary>
    Iqr,
    
    /// <summary>
    /// Percent change from last value (threshold = percent)
    /// </summary>
    PercentChange,
    
    /// <summary>
    /// Moving average deviation (threshold = percent deviation)
    /// </summary>
    MovingAverage
}

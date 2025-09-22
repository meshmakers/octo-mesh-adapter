using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for anomaly detection node
/// </summary>
public record AnomalyDetectionNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// List of detector configurations for different fields
    /// </summary>
    public List<DetectorConfiguration> Detectors { get; set; } = new();

    /// <summary>
    /// Stop pipeline execution if anomaly is detected
    /// </summary>
    public bool StopOnAnomaly { get; set; } = false;
    
    /// <summary>
    /// Set empty results array even if no anomalies are detected
    /// </summary>
    public bool SetEmptyResults { get; set; } = false;
}

/// <summary>
/// Configuration for individual anomaly detector
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record DetectorConfiguration
{
    /// <summary>
    /// JSONPath to the value to monitor
    /// </summary>
    public string Path { get; set; } = "";
    
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
    
    /// <summary>
    /// Optional key for statistics storage (defaults to Path if not specified)
    /// </summary>
    public string? StatisticsKey { get; set; }
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

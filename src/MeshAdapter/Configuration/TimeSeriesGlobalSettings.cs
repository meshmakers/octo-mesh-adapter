namespace Meshmakers.Octo.MeshAdapter.Configuration;

/// <summary>
/// This represents the status whether the time series is enabled or not.
/// This must not be in the TimeSeriesCk because the Ck may not be imported yet.
/// </summary>
internal class TimeSeriesGlobalSettings
{
    public static TimeSeriesGlobalSettings Enabled => new() { IsEnabled = true };
    public static TimeSeriesGlobalSettings Disabled => new() { IsEnabled = false };

    
    
    /// <summary>
    /// Time series enabled for a given tenant.
    /// </summary>
    public bool IsEnabled { get; set; }
}
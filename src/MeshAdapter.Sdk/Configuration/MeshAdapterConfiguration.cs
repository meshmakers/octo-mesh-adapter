namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

/// <summary>
/// Configuration for the mesh adapter.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class MeshAdapterConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MeshAdapterConfiguration"/> class.
    /// </summary>
    public MeshAdapterConfiguration()
    {
        StreamDataHost = "127.0.0.1";
        StreamDataUser = "crate";
    }

    /// <summary>
    /// Internal URI to the reporting service.
    /// </summary>
    public string ReportingServiceUrl { get; set; } = "https://localhost:5007";

    /// <summary>
    /// Hostname of crate db server
    /// </summary>
    public string StreamDataHost { get; set; }

    /// <summary>
    /// User of crate db
    /// </summary>
    public string StreamDataUser { get; set; }

    /// <summary>
    /// Password for crate db
    /// </summary>
    public string? StreamDataPassword { get; set; }
}
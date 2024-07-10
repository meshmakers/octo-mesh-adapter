namespace Meshmakers.Octo.MeshAdapter.Configuration;

public class MeshAdapterConfiguration
{
    public MeshAdapterConfiguration()
    {
        StreamDataHost = "127.0.0.1";
        StreamDataUser = "crate";
    }
    
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
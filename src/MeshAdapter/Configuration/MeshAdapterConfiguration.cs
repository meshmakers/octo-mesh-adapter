namespace Meshmakers.Octo.MeshAdapter.Configuration;

public class MeshAdapterConfiguration
{
    public string StreamDataConnectionString { get; set; }
    
    public MeshAdapterConfiguration()
    {
        StreamDataConnectionString = "Host=127.0.0.1;Username=crate;SSL Mode=Prefer";
    }
}
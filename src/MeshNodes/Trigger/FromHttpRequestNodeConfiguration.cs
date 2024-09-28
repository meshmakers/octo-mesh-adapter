using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromHttpRequest
/// </summary>
[NodeName("FromHttpRequest", 1)]
public record FromHttpRequestNodeConfiguration: TriggerNodeConfiguration
{
    /// <summary>
    /// Defines the HTTP action to be performed
    /// </summary>
    public HttpMethod Method { get; set; }
    
    /// <summary>
    /// Defines the path to be used
    /// </summary>
    public string Path { get; set; } = null!;
    
    
}
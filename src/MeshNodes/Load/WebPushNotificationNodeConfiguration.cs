using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for web push notifications
/// </summary>
[NodeName("WebPushNotification", 1)]
public record WebPushNotificationNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the public key for the voluntary application server identification
    /// </summary>
    public string? PublicKey { get; set; }
    
    /// <summary>
    /// Gets or sets the private key for the voluntary application server identification
    /// </summary>
    public string? PrivateKey { get; set; }
}
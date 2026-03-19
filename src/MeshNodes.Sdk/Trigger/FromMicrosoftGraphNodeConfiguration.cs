using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for polling Microsoft Teams channels via Microsoft Graph API
/// </summary>
[NodeName("FromMicrosoftGraph", 1)]
public record FromMicrosoftGraphNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// The global configuration key for the Microsoft Graph OAuth2 settings
    /// (references a MicrosoftGraphConfiguration entity by WellKnownName)
    /// </summary>
    public string ServerConfiguration { get; set; } = null!;

    /// <summary>
    /// Polling interval in seconds to check for new messages
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// The Microsoft Teams team ID (GUID)
    /// </summary>
    public required string TeamId { get; set; }

    /// <summary>
    /// The Microsoft Teams channel ID
    /// </summary>
    public required string ChannelId { get; set; }

    /// <summary>
    /// Optional filter for sender display name (contains match)
    /// </summary>
    public string? SenderFilter { get; set; }
}

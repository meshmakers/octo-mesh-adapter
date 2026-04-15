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
    [PropertyGroup("Connection", 0)]
    public string ServerConfiguration { get; set; } = null!;

    /// <summary>
    /// Polling interval in seconds to check for new messages
    /// </summary>
    [PropertyGroup("Timing", 0)]
    public int PollingIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// The Microsoft Teams team ID (GUID)
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string TeamId { get; set; }

    /// <summary>
    /// The Microsoft Teams channel ID
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public required string ChannelId { get; set; }

    /// <summary>
    /// Optional filter for sender display name (contains match)
    /// </summary>
    [PropertyGroup("Query", 0)]
    public string? SenderFilter { get; set; }
}

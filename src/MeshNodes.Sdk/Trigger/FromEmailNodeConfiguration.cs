using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromEmail
/// </summary>
[NodeName("FromEmail", 1)]
public record FromEmailNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// The global configuration key for the email server settings
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string ServerConfiguration { get; set; } = null!;

    /// <summary>
    /// Polling interval in seconds to check for new emails
    /// </summary>
    [PropertyGroup("Timing", 0)]
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to only process unread emails
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool OnlyUnread { get; set; } = true;

    /// <summary>
    /// Whether to mark emails as read after processing
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool MarkAsRead { get; set; } = true;

    /// <summary>
    /// Whether to delete emails after processing
    /// </summary>
    [PropertyGroup("Options", 2)]
    public bool DeleteAfterProcessing { get; set; } = false;

    /// <summary>
    /// Optional filter for sender email address (contains match)
    /// </summary>
    [PropertyGroup("Query", 0)]
    public string? SenderFilter { get; set; }

    /// <summary>
    /// Optional filter for email subject (contains match)
    /// </summary>
    [PropertyGroup("Query", 1)]
    public string? SubjectFilter { get; set; }
}
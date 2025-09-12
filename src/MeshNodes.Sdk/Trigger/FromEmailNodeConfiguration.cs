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
    public string ServerConfiguration { get; set; } = null!;
    
    /// <summary>
    /// Polling interval in seconds to check for new emails
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 60;
    
    /// <summary>
    /// Whether to only process unread emails
    /// </summary>
    public bool OnlyUnread { get; set; } = true;
    
    /// <summary>
    /// Whether to mark emails as read after processing
    /// </summary>
    public bool MarkAsRead { get; set; } = true;
    
    /// <summary>
    /// Whether to delete emails after processing
    /// </summary>
    public bool DeleteAfterProcessing { get; set; } = false;
    
    /// <summary>
    /// Optional filter for sender email address (contains match)
    /// </summary>
    public string? SenderFilter { get; set; }
    
    /// <summary>
    /// Optional filter for email subject (contains match)
    /// </summary>
    public string? SubjectFilter { get; set; }
}
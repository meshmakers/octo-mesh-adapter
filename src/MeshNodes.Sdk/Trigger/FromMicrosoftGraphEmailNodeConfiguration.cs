using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for polling an Office 365 mailbox folder via Microsoft Graph API.
/// Processes every message in the configured folder (the folder is the work queue —
/// no unread filtering) and optionally moves successfully processed messages to a
/// different folder.
/// </summary>
[NodeName("FromMicrosoftGraphEmail", 1)]
public record FromMicrosoftGraphEmailNodeConfiguration : TriggerNodeConfiguration
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
    /// The mailbox to poll (user principal name, e.g. user@company.com)
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string Mailbox { get; set; }

    /// <summary>
    /// Path of the mail folder to poll, segments separated by '/'
    /// (e.g. "Archive/Invoices/ToDo"). The path is resolved relative to the
    /// mailbox root — the pipeline never looks at the inbox unless the path
    /// points there.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public required string FolderPath { get; set; }

    /// <summary>
    /// Optional folder path the message is moved to after the pipeline run for
    /// that message completed successfully (e.g. "Archive/Invoices/Done").
    /// The leaf folder is created if it does not exist yet (its parent path must
    /// exist). Messages whose pipeline run failed stay in the source folder.
    /// </summary>
    [PropertyGroup("Connection", 3)]
    public string? MoveToFolderPathOnSuccess { get; set; }

    /// <summary>
    /// Maximum number of messages fetched per polling cycle (oldest first)
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int MaxMessagesPerPoll { get; set; } = 25;

    /// <summary>
    /// Optional filter for the sender address (contains match)
    /// </summary>
    [PropertyGroup("Query", 1)]
    public string? SenderFilter { get; set; }

    /// <summary>
    /// Number of times a failing message is retried (one attempt per polling
    /// cycle) before it is skipped until the adapter restarts
    /// </summary>
    [PropertyGroup("Query", 2)]
    public int MaxAttemptsPerMessage { get; set; } = 3;
}

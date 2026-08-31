using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for polling an Office 365 mailbox folder via Microsoft Graph API.
/// Processes every message in the configured folder (the folder is the work queue —
/// no unread filtering) and optionally moves successfully processed messages to a
/// different folder.
/// </summary>
[NodeName("FromMicrosoftGraphEmail", 1)]
[NodeRequiresRunningProcess]
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
    /// The mailbox to poll (user principal name, e.g. user@company.com).
    /// Optional when <see cref="SettingsConfiguration"/> supplies it — a value read
    /// from the settings configuration takes precedence over this one, so the mailbox
    /// need not (and should not) be hard-coded in the pipeline definition.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string Mailbox { get; set; } = null!;

    /// <summary>
    /// Path of the mail folder to poll, segments separated by '/'
    /// (e.g. "Archive/Invoices/ToDo"). The path is resolved relative to the
    /// mailbox root — the pipeline never looks at the inbox unless the path
    /// points there. Optional when <see cref="SettingsConfiguration"/> supplies it
    /// (the settings value takes precedence).
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string FolderPath { get; set; } = null!;

    /// <summary>
    /// Optional folder path the message is moved to after the pipeline run for
    /// that message completed successfully (e.g. "Archive/Invoices/Done").
    /// The leaf folder is created if it does not exist yet (its parent path must
    /// exist). Messages whose pipeline run failed stay in the source folder.
    /// A value from <see cref="SettingsConfiguration"/> takes precedence.
    /// </summary>
    [PropertyGroup("Connection", 3)]
    public string? MoveToFolderPathOnSuccess { get; set; }

    /// <summary>
    /// Optional well-known name of a configuration entity that carries the runtime
    /// mailbox / folder settings, so they live in configuration instead of the
    /// pipeline definition (a redeploy then never overwrites what an operator set,
    /// and nothing tenant-specific leaks into the seed). The configuration must be
    /// reachable from the pipeline through a <c>System.Communication/Uses</c>
    /// association. The node stays domain-agnostic: the attribute names it reads are
    /// given by <see cref="MailboxAttribute"/> / <see cref="SourceFolderAttribute"/> /
    /// <see cref="DoneFolderAttribute"/> (and optionally
    /// <see cref="PollingSecondsAttribute"/>). Values found here override the
    /// corresponding node properties above; a name that resolves to nothing falls
    /// back to the node property.
    /// </summary>
    [PropertyGroup("Settings", 0)]
    public string? SettingsConfiguration { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the mailbox (case-insensitive).</summary>
    [PropertyGroup("Settings", 1)]
    public string? MailboxAttribute { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the source folder path.</summary>
    [PropertyGroup("Settings", 2)]
    public string? SourceFolderAttribute { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the move-to-on-success folder path.</summary>
    [PropertyGroup("Settings", 3)]
    public string? DoneFolderAttribute { get; set; }

    /// <summary>
    /// Optional attribute name on <see cref="SettingsConfiguration"/> holding the poll
    /// interval in seconds. When present and a positive integer it overrides
    /// <see cref="PollingIntervalSeconds"/>.
    /// </summary>
    [PropertyGroup("Settings", 4)]
    public string? PollingSecondsAttribute { get; set; }

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

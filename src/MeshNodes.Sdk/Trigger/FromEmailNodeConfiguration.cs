using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromEmail
/// </summary>
[NodeName("FromEmail", 1)]
[NodeRequiresRunningProcess]
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
    /// Maximum number of messages fetched and dispatched in a single polling pass (AB#5336).
    /// Each message is downloaded in full and its attachments are base64-encoded into the batch, so an
    /// unbounded result set is an out-of-memory failure on any mailbox with history. A backlog is drained
    /// over consecutive polls instead. Values &lt;= 0 mean "no limit" and are not recommended.
    /// </summary>
    [PropertyGroup("Timing", 1)]
    public int MaxMessagesPerPoll { get; set; } = 25;

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

    /// <summary>
    /// Only consider messages delivered on or after this date (AB#5340). Maps to the IMAP <c>SINCE</c> key,
    /// which the server evaluates on the message's INTERNALDATE with date granularity and inclusive bounds.
    /// Takes precedence over <see cref="SinceDaysBack" />.
    /// </summary>
    [PropertyGroup("Query", 2)]
    public DateTime? SinceDate { get; set; }

    /// <summary>
    /// Only consider messages delivered within the last N days (AB#5340). Relative alternative to
    /// <see cref="SinceDate" />, which wins when both are configured. Values &lt;= 0 are ignored.
    /// </summary>
    [PropertyGroup("Query", 3)]
    public int? SinceDaysBack { get; set; }
}
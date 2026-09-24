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
    /// Default for <see cref="MaxMessagesPerPoll" />, resolved where the value is read.
    /// </summary>
    public const int DefaultMaxMessagesPerPoll = 25;

    /// <summary>
    /// Maximum number of messages fetched and dispatched in a single polling pass (AB#5336).
    /// Each message is downloaded in full and its attachments are base64-encoded into the batch, so an
    /// unbounded result set is an out-of-memory failure on any mailbox with history. A backlog is drained
    /// over consecutive polls instead. Values &lt;= 0 mean "no limit" and are not recommended.
    /// <para>
    /// Nullable with the default resolved at the call site on purpose: the pipeline definition
    /// deserializer is YamlDotNet, where a key that is PRESENT and null overwrites a property
    /// initializer. On a non-nullable int that yields 0 — which this node reads as the deliberate
    /// "no limit" opt-out, so an explicit null would silently switch the OOM protection back off.
    /// </para>
    /// </summary>
    [PropertyGroup("Timing", 1)]
    public int? MaxMessagesPerPoll { get; set; }

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
    /// Path into the pipeline's result data that CONFIRMS the batch was imported (AB#5337).
    /// <c>\Seen</c> / <c>\Deleted</c> are written back only when it resolves to the boolean
    /// <c>true</c>; anything else — absent, null, <c>false</c>, a string, a number — leaves the
    /// mails exactly as the server has them, so a later restart offers them again.
    /// <para>
    /// 🔴 Why this exists: a pipeline can end perfectly normally while a node reported an error and
    /// stopped its branch — that is what <c>MakeHttpRequest@1</c>'s <c>LogAndStop</c> is defined to
    /// do ("leaving the execution successful") — and the platform surfaces no per-node status to a
    /// trigger: the execution status is <c>Completed</c> unless something threw, and
    /// <c>ExecuteAsync</c> returns the pipeline's data root, nothing else. Without a confirmation
    /// the trigger cannot see such a branch, so this is the only way to make the write-back
    /// conditional on the import itself.
    /// </para>
    /// <para>
    /// ⚠️ UNSET keeps the pre-AB#5337 behaviour: a run that came back flags its mails. A stricter
    /// default would stop every deployed <c>FromEmail@1</c> from marking anything read and re-offer
    /// its whole <c>SINCE</c> window on every adapter restart — a certain fleet-wide regression
    /// traded for one edge case. What is NEVER traded is the run that threw: that one leaves the
    /// mailbox untouched whatever this is set to.
    /// </para>
    /// <para>
    /// AB#5345 supersedes this with a business success criterion ("an inbox item was created") and
    /// one post-processing mode shared by the IMAP and Graph channels; this property is the hook it
    /// can hang on and is deliberately not grown into that model here.
    /// </para>
    /// <para>
    /// Write the flag as the LAST step of the import branch (e.g.
    /// <c>SetPrimitiveValue@1 targetPath: $.importCompleted, value: true, valueType: Boolean</c>)
    /// — placed there, a branch that stopped early never reaches it.
    /// </para>
    /// <para>
    /// Syntax: a plain path from the pipeline data root, optionally with the <c>$.</c> prefix
    /// (<c>$.importCompleted</c>, <c>result.ok</c>). Array indexes, wildcards and filters are
    /// rejected when the trigger starts — a confirmation must name exactly one value.
    /// </para>
    /// </summary>
    [PropertyGroup("Options", 3)]
    public string? SuccessPath { get; set; }

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
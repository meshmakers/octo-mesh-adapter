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
    /// Whether to mark emails as read after a CONFIRMED import.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="PostProcessingMode" /> (AB#5345) and read only while no mode is
    /// set, where it derives <see cref="MailPostProcessingMode.MarkAsRead" />. Kept — rather than
    /// removed — because every deployed <c>FromEmail@1</c> names it, and a property a released
    /// node stops honouring turns other tenants' pipelines into silent behaviour changes.
    /// </remarks>
    [PropertyGroup("Options", 1)]
    public bool MarkAsRead { get; set; } = true;

    /// <summary>
    /// Whether to delete emails after a CONFIRMED import.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="PostProcessingMode" /> (AB#5345) and read only while no mode is
    /// set, where it derives <see cref="MailPostProcessingMode.Delete" /> and wins over
    /// <see cref="MarkAsRead" />.
    /// </remarks>
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
    /// AB#5345 built on exactly this hook: the flag the pipeline writes here is now the BUSINESS
    /// success criterion — "at least one inbox item was created" — and it gates every one of the
    /// three <see cref="PostProcessingMode" />s, on this channel and on the Microsoft Graph one.
    /// The evaluation itself moved to <c>MailSuccessPath</c>, which both triggers share.
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

    /// <summary>
    /// Whether this trigger currently has an import window at all (AB#5341). <c>false</c> means the
    /// window is EMPTY and the trigger fetches nothing: it skips the whole polling pass — no search,
    /// no download, no pipeline run — and picks up again by itself as soon as the value flips back.
    /// <para>
    /// This exists because "no cut-off" cannot express "import nothing". An absent
    /// <see cref="SinceDate" /> / <see cref="SinceDaysBack" /> means "no date filter", which with
    /// <see cref="OnlyUnread" /> switched off is the unbounded <c>SearchQuery.All</c> that killed the
    /// adapter on prod-1 with 1697 mails (AB#5336) — the exact opposite of what an empty window has
    /// to do. The accounting channel derives both values from the OPEN fiscal years, and "no fiscal
    /// year is open" has to mean nothing is imported, not everything.
    /// </para>
    /// <para>
    /// Deliberately NOT the operator's <c>System/Enabled</c> switch: that one is owned by whoever
    /// configures the channel, and flipping it from a computation would both fight the operator and
    /// require a DATA FLOW deploy to take effect (a disabled pipeline is dropped by
    /// <c>DeployDataFlow</c>), which restarts every sibling trigger. This value rides on the settings
    /// configuration instead, so a single-pipeline <c>DeployPipeline@1</c> delivers it.
    /// </para>
    /// <para>
    /// Nullable with the default resolved at the call site, for the YamlDotNet reason documented on
    /// <see cref="MaxMessagesPerPoll" />: a key that is PRESENT and null overwrites a property
    /// initializer, and on a non-nullable bool that yields <c>false</c> — which would silently
    /// switch a working mailbox off. Unset therefore reads as OPEN; see
    /// <c>FromEmailNode.ResolveWindowOpen</c>.
    /// </para>
    /// </summary>
    [PropertyGroup("Query", 4)]
    public bool? WindowOpen { get; set; }

    /// <summary>
    /// Post-processing applied to a message once its pipeline run confirmed the import
    /// (AB#5345) — the same three modes the Microsoft Graph channel offers, so a settings page
    /// can present ONE choice for both. Overrides <see cref="MarkAsRead" /> /
    /// <see cref="DeleteAfterProcessing" /> when set.
    /// <para>
    /// ⚠️ UNSET is the migration path and means "keep doing what this pipeline already did":
    /// the mode is then DERIVED from the two legacy flags —
    /// <see cref="DeleteAfterProcessing" /> ⇒ <see cref="MailPostProcessingMode.Delete" />,
    /// else <see cref="MarkAsRead" /> ⇒ <see cref="MailPostProcessingMode.MarkAsRead" />. Both flags
    /// on is not a lost combination: a deleted message is expunged, so the <c>\Seen</c> flag it
    /// would also have carried is not observable by anyone.
    /// </para>
    /// <para>
    /// 🔴 <b>There is no fourth mode that leaves the message alone, and neither flag set is a
    /// configuration error rather than a mode</b> (AB#5372): the trigger start FAILS naming the three
    /// valid modes. <c>None = 0</c> existed until then and was a defect. Nothing in OctoMesh records
    /// which mail was already processed — the mailbox IS the bookkeeping, the IMAP search is
    /// <c>NotSeen</c>, and the three modes work precisely because each takes the message out of that
    /// search result. Leaving it in re-fetches the same <see cref="MaxMessagesPerPoll" /> messages on
    /// every poll and never reaches the mail behind the cap, so the import runs for ever, reports
    /// success and imports nothing new — the shape of AB#5336.
    /// </para>
    /// <para>
    /// Still nullable and still only read through <c>ResolveEffectivePostProcessingMode</c>, never
    /// directly: the pipeline definition deserializer is YamlDotNet, where a key that is PRESENT and
    /// null overwrites a property initializer. On a non-nullable enum that would yield the zero
    /// value, which is now no member at all; nullable, such a key reads as "unset" and the
    /// derivation above decides — the same answer a pipeline that omits the key gets.
    /// </para>
    /// </summary>
    [PropertyGroup("Options", 4)]
    public MailPostProcessingMode? PostProcessingMode { get; set; }

    /// <summary>
    /// Folder polled as the work queue, overriding the <c>Folder</c> of the
    /// <see cref="ServerConfiguration" /> entity when set. Optional: the connection entity is
    /// still the place a plain single-folder import configures its folder; this exists because
    /// <see cref="MailPostProcessingMode.MoveToFolders" /> needs a source folder that belongs to
    /// the same triple as <see cref="DoneFolder" /> and <see cref="FailedFolder" />.
    /// </summary>
    [PropertyGroup("Folders", 0)]
    public string? SourceFolder { get; set; }

    /// <summary>
    /// Folder a message is moved to once its import was confirmed, under
    /// <see cref="MailPostProcessingMode.MoveToFolders" />. Segments separated by <c>/</c> and
    /// translated to the server's own hierarchy delimiter — the leaf is created when missing,
    /// its parents must exist.
    /// </summary>
    [PropertyGroup("Folders", 1)]
    public string? DoneFolder { get; set; }

    /// <summary>
    /// Folder a message is moved to when its import was NOT confirmed, under
    /// <see cref="MailPostProcessingMode.MoveToFolders" />. Left empty, such a message stays in
    /// the source folder.
    /// <para>
    /// Set it wherever the import matters: it is not only where a mail nobody could import is
    /// parked, it is also the way BACK — moving a message from here into the source folder offers
    /// it to the import again. Unlike the Graph channel this trigger does NOT count attempts per
    /// message (there is no IMAP counterpart to an Outlook category that survives a process death,
    /// and the IMAP batch is one execution for many messages rather than one per message), so a
    /// message whose batch was not confirmed is moved aside on that first unconfirmed run.
    /// </para>
    /// </summary>
    [PropertyGroup("Folders", 2)]
    public string? FailedFolder { get; set; }

    /// <summary>
    /// Optional well-known name of a configuration entity carrying the RUNTIME settings of this
    /// trigger, so they live in configuration instead of the pipeline definition (AB#5345). The
    /// configuration must be reachable from the pipeline through a
    /// <c>System.Communication/Uses</c> association.
    /// <para>
    /// 🔴 The rule this exists for: <b>setting a setting must never rewrite a pipeline
    /// definition.</b> The definition is release content — it is what a redeploy reinstates —
    /// while a poll interval or an "only unread" switch is operating state. Writing the latter
    /// into the former is how the accounting app moved one tenant's pipeline entity from v35 to
    /// v39 with nothing but a checkbox.
    /// </para>
    /// <para>
    /// The node stays domain-agnostic: which attributes to read is given by the
    /// <c>*Attribute</c> properties below. A value found there overrides the corresponding node
    /// property; anything missing falls back to the node property, which is what keeps every
    /// already deployed pipeline behaving exactly as before.
    /// </para>
    /// </summary>
    [PropertyGroup("Settings", 0)]
    public string? SettingsConfiguration { get; set; }

    /// <summary>Attribute holding the poll interval in seconds (positive integers only).</summary>
    [PropertyGroup("Settings", 1)]
    public string? PollingSecondsAttribute { get; set; }

    /// <summary>Attribute holding the "only unread" switch (<see cref="OnlyUnread" />).</summary>
    [PropertyGroup("Settings", 2)]
    public string? OnlyUnreadAttribute { get; set; }

    /// <summary>
    /// Attribute holding the post-processing mode NAME (<see cref="MailPostProcessingMode" />).
    /// Names only — a number there is ignored, see <c>ConfigurationSettingsReader.ReadEnum</c>.
    /// <para>
    /// ⚠️ An unknown name means "not configured" and hands the decision back to
    /// <see cref="PostProcessingMode" /> — with ONE exception: a stored <c>None</c> FAILS the trigger
    /// start (AB#5372). It was a real, storable mode until then, so reading it as "not configured"
    /// would silently replace an operator's "change nothing" with the derived mode and start writing
    /// to their mailbox.
    /// </para>
    /// </summary>
    [PropertyGroup("Settings", 3)]
    public string? PostProcessingModeAttribute { get; set; }

    /// <summary>Attribute holding the polled folder (<see cref="SourceFolder" />).</summary>
    [PropertyGroup("Settings", 4)]
    public string? SourceFolderAttribute { get; set; }

    /// <summary>Attribute holding the done folder (<see cref="DoneFolder" />).</summary>
    [PropertyGroup("Settings", 5)]
    public string? DoneFolderAttribute { get; set; }

    /// <summary>Attribute holding the failed folder (<see cref="FailedFolder" />).</summary>
    [PropertyGroup("Settings", 6)]
    public string? FailedFolderAttribute { get; set; }

    /// <summary>
    /// Attribute holding the per-poll batch cap (<see cref="MaxMessagesPerPoll" />). Read with
    /// the zero-tolerant integer reader, because a configured <c>0</c> is this property's
    /// deliberate "no limit" opt-out rather than an unset value.
    /// </summary>
    [PropertyGroup("Settings", 7)]
    public string? MaxMessagesPerPollAttribute { get; set; }

    /// <summary>Attribute holding the absolute date cut-off (<see cref="SinceDate" />).</summary>
    [PropertyGroup("Settings", 8)]
    public string? SinceDateAttribute { get; set; }

    /// <summary>Attribute holding the relative date cut-off (<see cref="SinceDaysBack" />).</summary>
    [PropertyGroup("Settings", 9)]
    public string? SinceDaysBackAttribute { get; set; }

    /// <summary>
    /// Attribute holding the import-window flag (<see cref="WindowOpen" />). A <c>false</c> there
    /// suspends fetching for as long as it stands; unset leaves the definition's value, which is
    /// "open".
    /// </summary>
    [PropertyGroup("Settings", 13)]
    public string? WindowOpenAttribute { get; set; }

    /// <summary>Attribute holding the sender filter (<see cref="SenderFilter" />).</summary>
    [PropertyGroup("Settings", 10)]
    public string? SenderFilterAttribute { get; set; }

    /// <summary>Attribute holding the subject filter (<see cref="SubjectFilter" />).</summary>
    [PropertyGroup("Settings", 11)]
    public string? SubjectFilterAttribute { get; set; }

    /// <summary>
    /// Attribute holding the import confirmation path (<see cref="SuccessPath" />). Rarely worth
    /// configuring — the path is a contract between this trigger and the pipeline it starts, both
    /// of which ship together — but it is a setting like the others and behaves like them.
    /// </summary>
    [PropertyGroup("Settings", 12)]
    public string? SuccessPathAttribute { get; set; }
}

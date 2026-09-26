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
    /// points there. A folder whose own name contains a slash is written with
    /// <c>\/</c> — "Inbox/02_Steuern \/ Finanzen" addresses the folder
    /// "02_Steuern / Finanzen" below the inbox (AB#5385) — and a backslash with
    /// <c>\\</c>; any other backslash is literal. The same escapes work in
    /// <see cref="MoveToFolderPathOnSuccess"/> and <see cref="MoveToFolderPathOnFailure"/>,
    /// and <c>ListMailFolders@1</c> emits paths in exactly this form (AB#5370).
    /// Optional when <see cref="SettingsConfiguration"/> supplies it
    /// (the settings value takes precedence).
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string FolderPath { get; set; } = null!;

    /// <summary>
    /// Optional folder path the message is moved to after the pipeline run for
    /// that message completed successfully (e.g. "Archive/Invoices/Done").
    /// The leaf folder is created if it does not exist yet (its parent path must
    /// exist). Messages whose pipeline run failed stay in the source folder.
    /// Same syntax as <see cref="FolderPath"/> (a slash inside a name is <c>\/</c>).
    /// A value from <see cref="SettingsConfiguration"/> takes precedence.
    /// </summary>
    [PropertyGroup("Connection", 3)]
    public string? MoveToFolderPathOnSuccess { get; set; }

    /// <summary>
    /// Optional folder path a message is moved to once it failed
    /// <see cref="MaxAttemptsPerMessage"/> times (e.g. "Archive/Invoices/Failed").
    /// The leaf folder is created if it does not exist yet (its parent path must
    /// exist). Without it, exhausted messages stay in the source folder and are
    /// skipped. Attempts are tracked on the message itself (an Outlook category
    /// marker), so the count survives adapter restarts — including runs that kill
    /// the process (e.g. an OOM) and therefore never report a failure.
    /// </summary>
    /// <remarks>
    /// Set this wherever the import matters: it is not only where poison mails are
    /// parked, it is also the <b>only user-facing way back</b>. A parked message is
    /// marked <c>OctoMesh-Import-Failed</c>, and moving it back into
    /// <see cref="FolderPath"/> clears every import marker and imports it again with a
    /// full attempt budget — no Graph access, no category editing, no adapter restart
    /// (AB#5260). Without a failure folder there is nowhere to move a message back
    /// <i>from</i>, and an exhausted message sits in the source folder unnoticed.
    /// A path that cannot be resolved degrades to "skip exhausted messages" and is
    /// logged; it never stops the import as a whole.
    /// </remarks>
    [PropertyGroup("Connection", 4)]
    public string? MoveToFolderPathOnFailure { get; set; }

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
    /// Optional attribute name on <see cref="SettingsConfiguration"/> holding the
    /// move-to-on-failure folder path (overrides
    /// <see cref="MoveToFolderPathOnFailure"/> when present, AB#5142).
    /// </summary>
    [PropertyGroup("Settings", 5)]
    public string? FailedFolderAttribute { get; set; }
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
    /// Number of times a failing message is tried (one attempt per polling
    /// cycle) before it is skipped — or moved to
    /// <see cref="MoveToFolderPathOnFailure"/> when that is configured. The
    /// attempt count is stamped on the message as an Outlook category before
    /// each run, so it survives adapter restarts and counts runs that never
    /// returned (process death).
    /// </summary>
    /// <remarks>
    /// The markers (<c>OctoMesh-Import-Attempt-N</c> and <c>OctoMesh-Import-Failed</c>)
    /// are registered in the mailbox's master category list on first use so Outlook and
    /// OWA actually render them — a category the mailbox does not know is invisible in
    /// the UI. That registration needs the <c>MailboxSettings.ReadWrite</c> Graph scope;
    /// without it the markers still count, they are merely invisible, and the node logs
    /// one warning. Removing a marker by hand resets the message on the next poll
    /// (AB#5260).
    /// </remarks>
    [PropertyGroup("Query", 2)]
    public int MaxAttemptsPerMessage { get; set; } = 3;

    /// <summary>
    /// Fetches the mail's internet message headers and surfaces the ones named in
    /// <see cref="InternetMessageHeaderNames"/> on <c>EmailData.Headers</c>, plus the parsed
    /// SPF/DKIM/DMARC verdicts on <c>EmailData.Authentication</c>. AB#5011.
    /// </summary>
    /// <remarks>
    /// Off by default and inert when off: an existing pipeline sees exactly the shape it saw before.
    /// Turn it on where the pipeline acts on the sender address — a sender gate, a per-vendor rule,
    /// anything that turns a mail into a document — because <c>From:</c> alone is a field anybody can
    /// write, and <c>Authentication-Results</c> is the only part of the mail that says whether the
    /// claimed sender really sent it.
    /// <para>
    /// Microsoft Graph does <b>not</b> return <c>internetMessageHeaders</c> unless it is selected
    /// explicitly, which is why the header was simply absent before this flag existed. Selecting it
    /// makes the per-message response noticeably larger (the full Received chain and the DKIM
    /// signatures come with it), which is why only the named headers are surfaced.
    /// </para>
    /// </remarks>
    [PropertyGroup("Query", 3)]
    public bool IncludeInternetMessageHeaders { get; set; }

    /// <summary>
    /// Header names surfaced on <c>EmailData.Headers</c> when
    /// <see cref="IncludeInternetMessageHeaders"/> is on. Case insensitive. Leave unset for the
    /// authentication-relevant default set (<c>Authentication-Results</c>,
    /// <c>Authentication-Results-Original</c>, <c>Received-SPF</c>, <c>ARC-Authentication-Results</c>).
    /// </summary>
    /// <remarks>
    /// A filter rather than "everything", because the headers land in the pipeline data context: the
    /// full set is several kilobytes of Received chain and base64 signatures per message, echoed into
    /// every debug view and persisted by <c>SetPipelineExecutionResult@1</c>.
    /// <c>Authentication-Results</c> is always fetched regardless of this list — it is what
    /// <c>EmailData.Authentication</c> is parsed from, and a list that omitted it would silently turn
    /// the verdicts off while the flag says they are on.
    /// </remarks>
    [PropertyGroup("Query", 4)]
    public string[]? InternetMessageHeaderNames { get; set; }

    /// <summary>
    /// Post-processing applied to a message once its pipeline run confirmed the import
    /// (AB#5345) — the same three modes the IMAP channel offers, so a settings page can present
    /// ONE choice for both. Overrides <see cref="MoveToFolderPathOnSuccess" /> /
    /// <see cref="MoveToFolderPathOnFailure" /> when set.
    /// <para>
    /// ⚠️ UNSET is the migration path and means "keep doing what this pipeline already did": the
    /// mode is then DERIVED — a configured done or failed folder ⇒
    /// <see cref="MailPostProcessingMode.MoveToFolders" />, which is byte for byte what this node
    /// did before.
    /// </para>
    /// <para>
    /// 🔴 <b>No folder at all is a configuration error rather than a mode</b> (AB#5372): the trigger
    /// start FAILS naming the three valid modes. <c>None = 0</c> existed until then — "leave it in
    /// the source folder" — and was a defect. Nothing in OctoMesh records which mail was already
    /// processed, the mailbox IS the bookkeeping, and the three modes work precisely because each
    /// takes the message out of what the next poll reads. Leaving it there re-reads the same
    /// <see cref="MaxMessagesPerPoll" /> messages for ever and never reaches the mail behind the cap,
    /// so the import runs for ever, reports success and imports nothing new (AB#5336).
    /// </para>
    /// <para>
    /// Still nullable and still only read through <c>ResolveEffectivePostProcessingMode</c>, never
    /// directly: the pipeline definition deserializer is YamlDotNet, where a key that is PRESENT and
    /// null overwrites a property initializer. On a non-nullable enum that would yield the zero
    /// value, which is now no member at all; nullable, such a key reads as "unset" and the
    /// derivation above decides.
    /// </para>
    /// <para>
    /// The attempt parking (<c>OctoMesh-Import-Attempt-N</c> / <c>OctoMesh-Import-Failed</c>
    /// categories, <see cref="MaxAttemptsPerMessage" />) belongs to
    /// <see cref="MailPostProcessingMode.MoveToFolders" /> and stays GRAPH-ONLY: it needs a
    /// per-message marker the mailbox persists across a process death, which is an Outlook
    /// category here and has no IMAP counterpart this node could rely on across servers. Under
    /// <see cref="MailPostProcessingMode.Delete" /> and
    /// <see cref="MailPostProcessingMode.MarkAsRead" /> there is nowhere to park a message, so the
    /// attempts are still counted and an exhausted message is skipped rather than moved.
    /// </para>
    /// </summary>
    [PropertyGroup("Query", 5)]
    public MailPostProcessingMode? PostProcessingMode { get; set; }

    /// <summary>
    /// Path into the pipeline's result data that CONFIRMS the message was imported (AB#5345,
    /// the same contract <c>FromEmail@1</c> has carried since AB#5337). The post-processing
    /// configured above happens only when it resolves to the boolean <c>true</c>; anything else —
    /// absent, null, <c>false</c>, a string, a number — leaves the message exactly where it is.
    /// <para>
    /// 🔴 Why this exists: this node used to decide done-or-failed on "<c>ExecuteAsync</c> did not
    /// throw", and that is not a statement about the import. A pipeline ends normally while a node
    /// reported an error and stopped its branch — <c>MakeHttpRequest@1</c>'s <c>LogAndStop</c> is
    /// defined to do that ("leaving the execution successful") — and no per-node status reaches a
    /// trigger. A mail could therefore be filed under "Done" with nothing in the inbox to show
    /// for it.
    /// </para>
    /// <para>
    /// ⚠️ UNSET keeps the pre-AB#5345 behaviour exactly: a run that came back post-processes its
    /// message. A stricter default would make every deployed <c>FromMicrosoftGraphEmail@1</c> stop
    /// moving anything and re-offer its whole source folder on every poll. What is NEVER traded is
    /// the run that threw: that one leaves the mailbox untouched whatever this is set to.
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
    [PropertyGroup("Query", 6)]
    public string? SuccessPath { get; set; }

    /// <summary>
    /// Attribute on <see cref="SettingsConfiguration" /> holding the post-processing mode NAME
    /// (<see cref="MailPostProcessingMode" />). Names only — a number there is ignored, see
    /// <c>ConfigurationSettingsReader.ReadEnum</c>.
    /// <para>
    /// ⚠️ An unknown name means "not configured" and hands the decision back to
    /// <see cref="PostProcessingMode" /> — with ONE exception: a stored <c>None</c> FAILS the trigger
    /// start (AB#5372), because reading a removed mode as "not configured" would silently replace it
    /// with the derived one.
    /// </para>
    /// </summary>
    [PropertyGroup("Settings", 6)]
    public string? PostProcessingModeAttribute { get; set; }

    /// <summary>Attribute holding the per-poll batch cap (<see cref="MaxMessagesPerPoll" />).</summary>
    [PropertyGroup("Settings", 7)]
    public string? MaxMessagesPerPollAttribute { get; set; }

    /// <summary>Attribute holding the sender filter (<see cref="SenderFilter" />).</summary>
    [PropertyGroup("Settings", 8)]
    public string? SenderFilterAttribute { get; set; }

    /// <summary>
    /// Attribute holding the import confirmation path (<see cref="SuccessPath" />). Rarely worth
    /// configuring — the path is a contract between this trigger and the pipeline it starts, both
    /// of which ship together — but it is a setting like the others and behaves like them.
    /// </summary>
    [PropertyGroup("Settings", 9)]
    public string? SuccessPathAttribute { get; set; }
}

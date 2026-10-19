using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// The settings <c>ListFailedImportMails@1</c> and <c>RequeueFailedImportMails@1</c> (AB#7087) share:
/// which channel, which stored mail configuration and which settings attributes name the mailbox, the
/// folder the import polls (the re-queue target) and the folder it parks mails it gave up on.
/// <para>
/// Nothing about the mailbox comes from the request. The channel is the only value a route may feed
/// in (<see cref="ChannelPath" />); mailbox, source folder, failed folder and credentials are read
/// from the configuration entities the pipeline <c>Uses</c> — the very ones the mail triggers read —
/// so an operator can only ever act on their own tenant's configured mailbox.
/// </para>
/// </summary>
public abstract record FailedImportMailsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Default for <c>MaxMails</c>, resolved where the value is read.</summary>
    public const int DefaultMaxMails = 200;

    /// <summary>Default for <c>TimeoutSeconds</c>, resolved where the value is read.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The channel name for an IMAP mailbox.</summary>
    public const string ChannelImap = "Imap";

    /// <summary>The channel name for a Microsoft 365 mailbox read through Microsoft Graph.</summary>
    public const string ChannelGraph = "Graph";

    /// <summary>Which mailbox: <c>Imap</c> or <c>Graph</c> (case-insensitive). Ignored when <see cref="ChannelPath" /> resolves.</summary>
    [PropertyGroup("Channel", 0)]
    public string? Channel { get; set; }

    /// <summary>JSONPath to the channel name in the data context, e.g. <c>$.body.channel</c>. Wins over <see cref="Channel" />.</summary>
    [PropertyGroup("Channel", 1)]
    public string? ChannelPath { get; set; }

    /// <summary>
    /// IMAP: well-known name of the <c>System.Communication/EMailReceiverConfiguration</c> the
    /// <c>FromEmail@1</c> trigger connects with.
    /// </summary>
    [PropertyGroup("IMAP", 0)]
    public string? ImapServerConfiguration { get; set; }

    /// <summary>IMAP: well-known name of the settings configuration the trigger reads its folders from (<c>settingsConfiguration</c>).</summary>
    [PropertyGroup("IMAP", 1)]
    public string? ImapSettingsConfiguration { get; set; }

    /// <summary>IMAP: attribute holding the folder the import polls — where a re-queued mail goes.</summary>
    [PropertyGroup("IMAP", 2)]
    public string? ImapSourceFolderAttribute { get; set; }

    /// <summary>IMAP: attribute holding the folder failed mails are parked in.</summary>
    [PropertyGroup("IMAP", 3)]
    public string? ImapFailedFolderAttribute { get; set; }

    /// <summary>
    /// Microsoft Graph: well-known name of the <c>System.Communication/MicrosoftGraphConfiguration</c>
    /// (the <c>FromMicrosoftGraphEmail@1</c> trigger's <c>serverConfiguration</c>).
    /// </summary>
    [PropertyGroup("Microsoft Graph", 0)]
    public string? GraphServerConfiguration { get; set; }

    /// <summary>Microsoft Graph: well-known name of the settings configuration the trigger reads its mailbox and folders from.</summary>
    [PropertyGroup("Microsoft Graph", 1)]
    public string? GraphSettingsConfiguration { get; set; }

    /// <summary>Microsoft Graph: attribute holding the mailbox address.</summary>
    [PropertyGroup("Microsoft Graph", 2)]
    public string? GraphMailboxAttribute { get; set; }

    /// <summary>Microsoft Graph: attribute holding the folder the import polls — where a re-queued mail goes.</summary>
    [PropertyGroup("Microsoft Graph", 3)]
    public string? GraphSourceFolderAttribute { get; set; }

    /// <summary>Microsoft Graph: attribute holding the folder failed mails are parked in.</summary>
    [PropertyGroup("Microsoft Graph", 4)]
    public string? GraphFailedFolderAttribute { get; set; }

    /// <summary>
    /// Upper bound on the mails handled in one call (default 200): listed, or re-queued by "all". The
    /// output says <c>truncated: true</c> when more are waiting. Nullable with the default resolved
    /// where it is read — the definition deserializer overwrites an initializer with a present null.
    /// </summary>
    [PropertyGroup("Limits", 0)]
    public int? MaxMails { get; set; }

    /// <summary>Overall time budget in seconds (default 120): connecting plus every request together.</summary>
    [PropertyGroup("Limits", 1)]
    public int? TimeoutSeconds { get; set; }
}

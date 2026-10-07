using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
///     Tests the connection of one document-import channel the way its trigger would use it, and
///     reports every step with its outcome (AB#5370 follow-up, 5.10.2026): the settings page's
///     "Test connection" button. Nothing is imported, sent or changed — the node signs in, lists or
///     asks, and signs out.
///     <para>Channels and their checks:</para>
///     <list type="bullet">
///         <item>
///             <c>Imap</c> — connect + log in with the <c>EMailReceiverConfiguration</c>
///             (<see cref="ImapServerConfiguration" />), list the folders, and look for the source
///             folder the trigger would poll (the settings attribute, else the server
///             configuration's default folder).
///         </item>
///         <item>
///             <c>Graph</c> — acquire the app token with the <c>MicrosoftGraphConfiguration</c>
///             (<see cref="GraphServerConfiguration" />), list the folders of the mailbox the
///             trigger reads (settings attribute, else <see cref="GraphMailbox" />), and look for
///             the configured source folder.
///         </item>
///         <item>
///             <c>Teams</c> — the same app registration as its bot identity: the Graph token the
///             bot downloads attachments with, and the Bot Framework token it replies with.
///         </item>
///         <item>
///             <c>Signal</c> — resolve the tenant's registered <c>SignalChannel</c>, ask the bridge
///             for its version (<c>GET /v1/about</c>) and check that the number is an account on it
///             (<c>GET /v1/accounts</c>).
///         </item>
///     </list>
///     <para>
///     Output at <see cref="TargetPathNodeConfiguration.TargetPath" />:
///     <c>{ channel, ok, summary, checks: [ { name, ok, detail } ] }</c>. A failed check is a
///     RESULT (<c>ok: false</c> with the operator-worded reason on the check), not a thrown node
///     error — the page shows every step, including the ones that passed before the failing one.
///     Only a misconfigured node (unknown channel, missing configuration reference) throws.
///     </para>
/// </summary>
[NodeName("TestImportConnection", 1)]
public record TestImportConnectionNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Default budget for the whole test, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>The IMAP e-mail assistant channel.</summary>
    public const string ChannelImap = "Imap";

    /// <summary>The Microsoft 365 (Graph) e-mail import channel.</summary>
    public const string ChannelGraph = "Graph";

    /// <summary>The Microsoft Teams bot channel.</summary>
    public const string ChannelTeams = "Teams";

    /// <summary>The Signal assistant channel.</summary>
    public const string ChannelSignal = "Signal";

    /// <summary>The channel to test, when the pipeline tests one fixed channel.</summary>
    [PropertyGroup("Channel", 0)]
    public string? Channel { get; set; }

    /// <summary>Data-context path carrying the channel, e.g. <c>$.body.channel</c> of an HTTP route.</summary>
    [PropertyGroup("Channel", 1)]
    public string? ChannelPath { get; set; }

    /// <summary>Well-known name of the <c>System.Communication/EMailReceiverConfiguration</c> the IMAP trigger uses.</summary>
    [PropertyGroup("IMAP", 0)]
    public string? ImapServerConfiguration { get; set; }

    /// <summary>Well-known name of the IMAP channel's settings configuration (its source folder lives there).</summary>
    [PropertyGroup("IMAP", 1)]
    public string? ImapSettingsConfiguration { get; set; }

    /// <summary>Attribute on <see cref="ImapSettingsConfiguration" /> carrying the source folder.</summary>
    [PropertyGroup("IMAP", 2)]
    public string? ImapSourceFolderAttribute { get; set; }

    /// <summary>Well-known name of the <c>System.Communication/MicrosoftGraphConfiguration</c> (shared by the Microsoft 365 import and the Teams bot).</summary>
    [PropertyGroup("Microsoft Graph", 0)]
    public string? GraphServerConfiguration { get; set; }

    /// <summary>Fallback mailbox when the settings carry none.</summary>
    [PropertyGroup("Microsoft Graph", 1)]
    public string? GraphMailbox { get; set; }

    /// <summary>Well-known name of the Microsoft 365 channel's settings configuration.</summary>
    [PropertyGroup("Microsoft Graph", 2)]
    public string? GraphSettingsConfiguration { get; set; }

    /// <summary>Attribute on <see cref="GraphSettingsConfiguration" /> carrying the mailbox.</summary>
    [PropertyGroup("Microsoft Graph", 3)]
    public string? GraphMailboxAttribute { get; set; }

    /// <summary>Attribute on <see cref="GraphSettingsConfiguration" /> carrying the source folder.</summary>
    [PropertyGroup("Microsoft Graph", 4)]
    public string? GraphSourceFolderAttribute { get; set; }

    /// <summary>Whole test, every step together.</summary>
    [PropertyGroup("Limits", 0)]
    public int? TimeoutSeconds { get; set; }
}

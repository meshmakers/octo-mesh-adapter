using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node ListMailFolders (AB#5370): lists the folders of a mailbox in EXACTLY the
/// syntax the corresponding mail trigger accepts, so an operator picks a folder from the server's
/// own list instead of guessing its path syntax.
/// <para>
/// Two channels, two syntaxes, both guessed wrong in production: the IMAP source folder is handed
/// to the server verbatim and has to carry the server's own hierarchy delimiter (Dovecot:
/// <c>INBOX.Finanzen.Rechnungen</c>), while the Microsoft 365 trigger splits its path on <c>/</c>
/// — an operator who copied the IMAP hint into the Graph field had every poll fail for days.
/// </para>
/// <para>
/// Output at <see cref="TargetPathNodeConfiguration.TargetPath" />:
/// <c>{ channel, delimiter, folders: [ { path, displayName, depth } ], truncated }</c>, where
/// <c>path</c> is the string to store in the trigger's folder setting. Read-only — the node never
/// creates, moves or selects anything.
/// </para>
/// </summary>
[NodeName("ListMailFolders", 1)]
public record ListMailFoldersNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Default for <see cref="MaxFolders" />, resolved where the value is read.</summary>
    public const int DefaultMaxFolders = 500;

    /// <summary>Default for <see cref="TimeoutSeconds" />, resolved where the value is read.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>The channel name for an IMAP mailbox.</summary>
    public const string ChannelImap = "Imap";

    /// <summary>The channel name for a Microsoft 365 mailbox read through Microsoft Graph.</summary>
    public const string ChannelGraph = "Graph";

    /// <summary>
    /// Which mailbox to list: <c>Imap</c> or <c>Graph</c> (case-insensitive). Ignored when
    /// <see cref="ChannelPath" /> resolves to a value.
    /// </summary>
    [PropertyGroup("Channel", 0)]
    public string? Channel { get; set; }

    /// <summary>
    /// JSONPath to the channel name in the data context, e.g. <c>$.body.channel</c> for a route
    /// whose request body carries it. Takes precedence over <see cref="Channel" /> when it
    /// resolves to a non-empty value.
    /// </summary>
    [PropertyGroup("Channel", 1)]
    public string? ChannelPath { get; set; }

    /// <summary>
    /// IMAP: well-known name of the <c>System.Communication/EMailReceiverConfiguration</c> the
    /// <c>FromEmail@1</c> trigger connects with (host, port, user name, password, SSL) — the same
    /// reference the trigger's <c>serverConfiguration</c> names, so the list comes from the mailbox
    /// the import will poll. Must be reachable from the pipeline through a
    /// <c>System.Communication/Uses</c> association.
    /// </summary>
    [PropertyGroup("IMAP", 0)]
    public string? ImapServerConfiguration { get; set; }

    /// <summary>
    /// Microsoft Graph: well-known name of the <c>System.Communication/MicrosoftGraphConfiguration</c>
    /// carrying the app registration (Azure tenant ID, client ID, client secret) — the same reference
    /// the <c>FromMicrosoftGraphEmail@1</c> trigger's <c>serverConfiguration</c> names.
    /// </summary>
    [PropertyGroup("Microsoft Graph", 0)]
    public string? GraphServerConfiguration { get; set; }

    /// <summary>
    /// Microsoft Graph: the mailbox (user principal name or address) whose folders are listed.
    /// Fallback only — a value read through <see cref="GraphSettingsConfiguration" /> /
    /// <see cref="GraphMailboxAttribute" /> wins, exactly as it does on the trigger.
    /// </summary>
    [PropertyGroup("Microsoft Graph", 1)]
    public string? GraphMailbox { get; set; }

    /// <summary>
    /// Microsoft Graph: optional well-known name of the settings configuration the trigger reads
    /// its mailbox from (its <c>settingsConfiguration</c>), so this node lists the folders of the
    /// mailbox the operator configured rather than one repeated in the definition.
    /// </summary>
    [PropertyGroup("Microsoft Graph", 2)]
    public string? GraphSettingsConfiguration { get; set; }

    /// <summary>
    /// Microsoft Graph: attribute on <see cref="GraphSettingsConfiguration" /> holding the mailbox
    /// (case-insensitive) — the trigger's <c>mailboxAttribute</c>.
    /// </summary>
    [PropertyGroup("Microsoft Graph", 3)]
    public string? GraphMailboxAttribute { get; set; }

    /// <summary>
    /// Upper bound on the number of folders returned (default 500). The walk stops there and the
    /// output says so with <c>truncated: true</c> — a mailbox with thousands of folders would
    /// otherwise turn a picker into a download. Nullable with the default resolved where it is
    /// read, because the pipeline definition deserializer is YamlDotNet and a key that is PRESENT
    /// and null overwrites a property initializer.
    /// </summary>
    [PropertyGroup("Limits", 0)]
    public int? MaxFolders { get; set; }

    /// <summary>
    /// Overall time budget for the whole listing in seconds (default 60): connecting,
    /// authenticating and every folder request together. A mail server that accepts the connection
    /// and never answers otherwise keeps the HTTP route open for as long as the caller waits.
    /// </summary>
    [PropertyGroup("Limits", 1)]
    public int? TimeoutSeconds { get; set; }
}

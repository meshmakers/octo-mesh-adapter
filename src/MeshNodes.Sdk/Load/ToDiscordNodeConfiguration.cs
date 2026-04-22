using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for the ToDiscord pipeline node.
/// </summary>
/// <remarks>
/// Resolves a <c>DiscordConfiguration</c> CK entity by name via global configuration
/// (see <see cref="ServerConfiguration"/>). Message fields follow the
/// <c>{Field}</c> + <c>{Field}Path</c> convention: when the *Path variant is
/// non-empty the value is read from the data context; otherwise the literal is used.
/// </remarks>
[NodeName("ToDiscord", 1)]
public record ToDiscordNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the global configuration entry that holds the <c>DiscordConfiguration</c>
    /// (bot token + optional guild id).
    /// </summary>
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds. Default 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Literal channel id (Discord snowflake) to post to. Threads are channels in
    /// Discord's data model, so pass a thread snowflake here to post into a thread —
    /// the same endpoint is used. (<c>?thread_id=</c> is webhook-only and silently
    /// ignored on bot-token calls, which is why a dedicated thread field is not offered.)
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// JSONPath to resolve the channel id from the data context.
    /// </summary>
    public string? ChannelIdPath { get; set; }

    /// <summary>
    /// Literal message content.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// JSONPath to resolve the message content from the data context.
    /// </summary>
    public string? ContentPath { get; set; }

    /// <summary>
    /// Literal embed title.
    /// </summary>
    public string? EmbedTitle { get; set; }

    /// <summary>
    /// JSONPath to resolve the embed title from the data context.
    /// </summary>
    public string? EmbedTitlePath { get; set; }

    /// <summary>
    /// Literal embed description.
    /// </summary>
    public string? EmbedDescription { get; set; }

    /// <summary>
    /// JSONPath to resolve the embed description from the data context.
    /// </summary>
    public string? EmbedDescriptionPath { get; set; }

    /// <summary>
    /// Literal embed color as a 24-bit RGB integer.
    /// </summary>
    public int? EmbedColor { get; set; }

    /// <summary>
    /// JSONPath to resolve the embed color from the data context.
    /// Accepts <c>0xRRGGBB</c>, <c>#RRGGBB</c>, or decimal integer strings.
    /// </summary>
    public string? EmbedColorPath { get; set; }

    /// <summary>
    /// RtId of a <c>System.Reporting/FileSystemItem</c> whose bound binary is posted as the message's
    /// single attachment. The node resolves the binary via <c>Content.BinaryId</c>. The filename sent
    /// to Discord is picked by precedence:
    /// <list type="number">
    /// <item><see cref="AttachmentFilename"/> / <see cref="AttachmentFilenamePath"/> when set.</item>
    /// <item>The FileSystemItem's <c>Name</c> attribute — the intentional, renameable display label.</item>
    /// <item>The FileSystemItem's <c>Content.Filename</c> — the ingest-time blob metadata (fallback).</item>
    /// </list>
    /// Requires the <c>System.Reporting</c> CK package to be loaded on the tenant.
    /// </summary>
    public string? AttachmentFileSystemItemRtId { get; set; }

    /// <summary>
    /// JSONPath to resolve the FileSystemItem RtId from the data context.
    /// </summary>
    public string? AttachmentFileSystemItemRtIdPath { get; set; }

    /// <summary>
    /// Optional override for the multipart filename sent to Discord. When set, takes precedence over
    /// both the FileSystemItem's <c>Name</c> and <c>Content.Filename</c>. Useful when the caller knows
    /// the user-facing name from domain metadata (e.g. an invoice number) that isn't reflected on the
    /// FileSystemItem itself.
    /// </summary>
    public string? AttachmentFilename { get; set; }

    /// <summary>
    /// JSONPath to resolve the attachment filename override from the data context.
    /// </summary>
    public string? AttachmentFilenamePath { get; set; }

    /// <summary>
    /// Controls which mentions in the message are allowed to ping recipients.
    /// Default is <see cref="MentionPolicy.None"/> — safest for pipelines that echo
    /// upstream data, which often contains unintentional or injected <c>@everyone</c>.
    /// Set to <see cref="MentionPolicy.Custom"/> to provide a raw Discord
    /// <c>allowed_mentions</c> object via <see cref="AllowedMentionsPath"/>.
    /// </summary>
    public MentionPolicy MentionPolicy { get; set; } = MentionPolicy.None;

    /// <summary>
    /// JSONPath resolving to a raw Discord <c>allowed_mentions</c> object. Only consulted
    /// when <see cref="MentionPolicy"/> is <see cref="MentionPolicy.Custom"/>; ignored otherwise.
    /// </summary>
    public string? AllowedMentionsPath { get; set; }
}

/// <summary>
/// High-level mention-filtering policy for <c>ToDiscord@1</c>. Applied by setting the
/// Discord <c>allowed_mentions.parse</c> field appropriately.
/// </summary>
public enum MentionPolicy
{
    /// <summary>No mentions in the message ping anyone (default). Sends <c>parse: []</c>.</summary>
    None = 0,

    /// <summary>Only user mentions (<c>&lt;@id&gt;</c>) can ping. Sends <c>parse: ["users"]</c>.</summary>
    Users = 1,

    /// <summary>Only role mentions (<c>&lt;@&amp;id&gt;</c>) can ping. Sends <c>parse: ["roles"]</c>.</summary>
    Roles = 2,

    /// <summary>User and role mentions can ping; <c>@everyone</c>/<c>@here</c> is suppressed.
    /// Sends <c>parse: ["users","roles"]</c>.</summary>
    UsersAndRoles = 3,

    /// <summary>All mentions ping, including <c>@everyone</c> and <c>@here</c>.
    /// Omits <c>allowed_mentions</c>; matches Discord's default.</summary>
    All = 4,

    /// <summary>Use the raw object resolved from <c>AllowedMentionsPath</c>.</summary>
    Custom = 5,
}

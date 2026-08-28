using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for node ResolveNotificationPlaceholders@1.
///
/// One resolver for every send path, which is what AB#2569 asks for: "placeholder resolution
/// must happen where the email is rendered, so that ALL send paths share one resolver". Before
/// this, each send pipeline carried its own generated <c>PlaceholderReplace@1</c> rule blocks
/// plus hand-written converter nodes, so a fourth send path meant a fourth copy and a token
/// wired in one pipeline but not another produced different mail from the same template.
///
/// A resolver *pipeline* called through <c>ToPipelineDataEvent@1 awaitResult</c> cannot be
/// shared: that node requires the target to live in the same DataFlow and names its queues per
/// DataFlow, and the send pipelines sit in three different ones.
///
/// Only the sources a path actually has are configured. A token whose source is not configured
/// fails the node rather than rendering as a blank - see the node's own documentation.
/// </summary>
[NodeName("ResolveNotificationPlaceholders", 1)]
public record ResolveNotificationPlaceholdersNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Path to the subject template, as <c>GetNotificationTemplate@1</c> wrote it.
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string SubjectPath { get; set; }

    /// <summary>
    /// Where the resolved subject is written.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string SubjectTargetPath { get; set; }

    /// <summary>
    /// Path to the body template.
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public required string BodyPath { get; set; }

    /// <summary>
    /// Where the resolved body is written.
    /// </summary>
    [PropertyGroup("Paths", 3, "jsonpath")]
    public required string BodyTargetPath { get; set; }

    /// <summary>
    /// Path to the entity carrying the recipient's contact record - a customer on a bulk send,
    /// or the billing document itself on a dispatch, since the document keeps the contact
    /// snapshot the invoice was issued with.
    /// </summary>
    [PropertyGroup("Sources", 0, "jsonpath")]
    public string? CustomerPath { get; set; }

    /// <summary>
    /// Path to the community configuration entity.
    /// </summary>
    [PropertyGroup("Sources", 1, "jsonpath")]
    public string? CommunityConfigPath { get; set; }

    /// <summary>
    /// Path to the billing document, on a path that sends one.
    /// </summary>
    [PropertyGroup("Sources", 2, "jsonpath")]
    public string? BillingDocumentPath { get; set; }

    /// <summary>
    /// Path to the template's rendering type, as written by
    /// <c>GetNotificationTemplate@1.RenderingTypeTargetPath</c>. Only an <c>Html</c> template
    /// can show the community logo; in a plain-text one that token renders as nothing rather
    /// than as markup the reader would see as characters.
    /// </summary>
    [PropertyGroup("Sources", 3, "jsonpath")]
    public string? RenderingTypePath { get; set; }

    /// <summary>
    /// Content id the send node attaches the community image under, and which
    /// <c>${community.logo}</c> addresses. Must match the attachment entry on
    /// <c>SendEMail@2</c>.
    /// </summary>
    [PropertyGroup("Sources", 4)]
    public string LogoContentId { get; set; } = "community-footer";
}

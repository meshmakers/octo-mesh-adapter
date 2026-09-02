using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for node SendEMail@2.
///
/// v1 carries exactly one attachment, described by four sibling properties, and offers no way
/// to reference it from the body. A billing dispatch therefore spends its only slot on the
/// invoice PDF and cannot also carry the community logo the template wants to show (AB#2570).
/// v2 replaces the four properties with a list, and lets an entry declare a
/// <see cref="EMailAttachment.ContentId"/> so the body can address it as <c>cid:&lt;id&gt;</c>.
/// </summary>
[NodeName("SendEMail", 2)]
public record EMailSenderNodeConfiguration2 : PathNodeConfiguration
{
    /// <summary>
    /// Well-known name of the EMailSenderConfiguration entity associated with the pipeline.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Path to the subject line.
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string SubjectPath { get; set; }

    /// <summary>
    /// Path to the recipient address or array of addresses.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string ToPath { get; set; }

    /// <summary>
    /// Path to a CC address or array of addresses.
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string? CcPath { get; set; }

    /// <summary>
    /// Literal CC addresses. Used INSTEAD of <see cref="CcPath"/> when non-empty, not in
    /// addition to it - same precedence as v1.
    /// </summary>
    [PropertyGroup("Recipients", 0)]
    public ICollection<string>? CcAddresses { get; set; }

    /// <summary>
    /// Path to a BCC address or array of addresses.
    /// </summary>
    [PropertyGroup("Paths", 3, "jsonpath")]
    public string? BccPath { get; set; }

    /// <summary>
    /// Literal BCC addresses. Used INSTEAD of <see cref="BccPath"/> when non-empty, not in
    /// addition to it - same precedence as v1.
    /// </summary>
    [PropertyGroup("Recipients", 1)]
    public ICollection<string>? BccAddresses { get; set; }

    /// <summary>
    /// Files to attach. An entry with a <see cref="EMailAttachment.ContentId"/> is linked into
    /// the HTML body instead of being listed as a separate file.
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    [PropertyGroup("Attachments", 0)]
    public List<EMailAttachment> Attachments { get; set; } = [];

    /// <summary>
    /// What the body at <c>Path</c> actually is.
    ///
    /// v1 ran Markdig over every body regardless. A NotificationTemplate carries a RenderingType
    /// for exactly this decision, and it used not to reach the sender at all, because
    /// GetNotificationTemplate@1 forwarded only subject and body - so a template declaring PLAIN
    /// was still sent as converted HTML. That was not only cosmetic: an author writing "A)" got
    /// an ordered list, and a literal <c>{netto=100}</c> was absorbed into the enclosing tag as
    /// an HTML attribute by Markdig's generic-attributes extension. The node now forwards it via
    /// <c>RenderingTypeTargetPath</c>, which <see cref="BodyFormatPath"/> reads.
    /// </summary>
    [PropertyGroup("Body", 0)]
    public BodyFormats BodyFormat { get; set; } = BodyFormats.Markdown;

    /// <summary>
    /// Path to a NotificationTemplate's RenderingType, as written by
    /// <c>GetNotificationTemplate@1</c>. When it resolves, it wins over
    /// <see cref="BodyFormat"/> - the template says what it is, the node should not overrule
    /// it. Plain renders as text, Html goes through Markdig, anything else falls back.
    /// </summary>
    [PropertyGroup("Paths", 5, "jsonpath")]
    public string? BodyFormatPath { get; set; }

    /// <summary>
    /// Path to a Reply-To address. A tenant usually sends from a no-reply mailbox while wanting
    /// answers to reach its own contact address.
    /// </summary>
    [PropertyGroup("Paths", 4, "jsonpath")]
    public string? ReplyToPath { get; set; }

    /// <summary>
    /// Literal Reply-To address, used when <see cref="ReplyToPath"/> is not set.
    /// </summary>
    [PropertyGroup("Recipients", 2)]
    public string? ReplyToAddress { get; set; }
}

/// <summary>
/// How the sender should treat the body it is given.
/// </summary>
public enum BodyFormats
{
    /// <summary>Convert with Markdig, as v1 always did.</summary>
    Markdown = 0,

    /// <summary>
    /// Escape it and keep the line breaks. What the author typed is what the recipient reads.
    /// </summary>
    PlainText = 1,

    /// <summary>Already HTML; send it unchanged.</summary>
    Html = 2
}

/// <summary>
/// One file to attach, addressed by the id of the stored binary rather than by the entity that
/// owns it - the same shape v1 resolved, since <c>AttachmentRtIdPath</c> always pointed at a
/// <c>…Content.BinaryId</c>.
/// </summary>
public record EMailAttachment
{
    /// <summary>
    /// Literal id of the stored binary. Mutually exclusive with <see cref="BinaryIdPath"/>.
    /// </summary>
    public string? BinaryId { get; set; }

    /// <summary>
    /// Path to the id of the stored binary in the data context.
    /// </summary>
    public string? BinaryIdPath { get; set; }

    /// <summary>
    /// File name the recipient sees on a file attachment.
    ///
    /// Ignored for an inline entry: a linked resource is identified by its
    /// <see cref="ContentId"/> and carries no <c>Content-Disposition</c>, so nothing in the
    /// message ever names it.
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// Path to the stored binary's own file name, which wins over <see cref="FileName"/> when it
    /// resolves. Ignored for an inline entry, for the reason given on <see cref="FileName"/>.
    /// </summary>
    public string? FileNamePath { get; set; }

    /// <summary>
    /// MIME type the recipient's client uses to decide how to show the file. An inline image
    /// needs a real one (e.g. <c>image/png</c>); the default is deliberately opaque.
    /// </summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Path to the stored binary's own MIME type, which wins over <see cref="ContentType"/> when
    /// it resolves. A logo is uploaded through the branding screen, where nothing stops an
    /// operator swapping a PNG for a JPEG - and a literal that then lies about the format is how
    /// an inline image stops rendering in Outlook.
    /// </summary>
    public string? ContentTypePath { get; set; }

    /// <summary>
    /// Makes the attachment addressable from the body as <c>cid:&lt;ContentId&gt;</c>, which is
    /// what renders an image in Outlook and Gmail without the external-image prompt. Leave unset
    /// for an ordinary file attachment such as an invoice PDF.
    /// </summary>
    public string? ContentId { get; set; }

    /// <summary>
    /// Send the mail anyway when the binary is not there. A tenant that never uploaded a logo
    /// must still receive its billing mail; the body's <c>cid:</c> reference is stripped so the
    /// recipient sees no broken image (AB#2570). A required attachment that is missing fails the
    /// send instead, because an invoice mail without its invoice is worse than no mail.
    /// </summary>
    public bool Optional { get; set; }
}

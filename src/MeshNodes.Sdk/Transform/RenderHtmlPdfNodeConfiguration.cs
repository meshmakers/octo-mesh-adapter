using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>RenderHtmlPdf@1</c>. Renders an HTML (or plain-text)
/// document into a single base64-encoded PDF using AngleSharp (HTML parsing) and
/// QuestPDF (layout). Cross-platform and browser-free — a pragmatic subset of
/// HTML is supported (headings, paragraphs, line breaks, bold/italic/underline,
/// links, ordered/unordered lists, tables, blockquotes, preformatted text,
/// horizontal rules and inline <c>data:</c>-URI images). Modern CSS layout
/// (flexbox/grid) and remote images are intentionally not rendered.
/// <para>
/// The value at <see cref="SourceTargetPathNodeConfiguration.Path"/> is the HTML
/// or plain-text content. When it contains no markup it is rendered as
/// preformatted text (line breaks preserved). Set <see cref="IsHtml"/> /
/// <see cref="IsHtmlPath"/> to override the automatic detection. An optional
/// <see cref="Title"/> / <see cref="TitlePath"/> is rendered as a heading above
/// the content — used to put the mail subject on the receipt. The base64-encoded
/// PDF is written to <see cref="TargetPathNodeConfiguration.TargetPath"/>.
/// </para>
/// </summary>
[NodeName("RenderHtmlPdf", 1)]
public record RenderHtmlPdfNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Optional heading rendered above the content (e.g. the mail subject).
    /// </summary>
    [PropertyGroup("Content", 0)]
    public string? Title { get; set; }

    /// <summary>
    /// Optional JSONPath to read the heading from at runtime. Takes precedence
    /// over <see cref="Title"/> when the path resolves to a non-empty value.
    /// </summary>
    [PropertyGroup("Content", 1, "jsonpath")]
    public string? TitlePath { get; set; }

    /// <summary>
    /// Explicit hint whether the content at <c>Path</c> is HTML. When null
    /// (default) the node auto-detects markup; set <c>true</c>/<c>false</c> to
    /// force HTML or plain-text rendering.
    /// </summary>
    [PropertyGroup("Content", 2)]
    public bool? IsHtml { get; set; }

    /// <summary>
    /// Optional JSONPath to a boolean deciding whether the content is HTML.
    /// Takes precedence over <see cref="IsHtml"/> when it resolves to a value.
    /// </summary>
    [PropertyGroup("Content", 3, "jsonpath")]
    public string? IsHtmlPath { get; set; }

    /// <summary>
    /// Optional path to write the rendered PDF's byte length to (as a long), for
    /// feeding a following <c>CreateFileSystemUpdate@1</c>.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }
}

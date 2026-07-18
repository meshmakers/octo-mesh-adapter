using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Document = QuestPDF.Fluent.Document;
using IElement = AngleSharp.Dom.IElement;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Renders an HTML (or plain-text) document to a single base64-encoded PDF using
/// AngleSharp for parsing and QuestPDF for layout. Browser-free and
/// cross-platform. A pragmatic subset of HTML is supported — see
/// <see cref="RenderHtmlPdfNodeConfiguration"/>. Used to turn a forwarded e-mail
/// that carries no attachment into an accounting receipt (the mail body itself).
/// </summary>
[NodeConfiguration(typeof(RenderHtmlPdfNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class RenderHtmlPdfNode(NodeDelegate next) : IPipelineNode
{
    // A4 (595.28pt) minus the 2cm margins on both sides ≈ 481.9pt of content width.
    // Images larger than this are scaled down; smaller images keep their natural size.
    private const float ContentWidthPt = 480f;

    static RenderHtmlPdfNode()
    {
        // meshmakers GmbH qualifies for the free QuestPDF Community license.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static readonly HashSet<string> InlineTags =
    [
        "a", "b", "strong", "i", "em", "u", "ins", "span", "font", "small",
        "sub", "sup", "mark", "code", "label", "abbr", "cite", "q"
    ];

    private static readonly HashSet<string> SkippedTags =
        ["script", "style", "head", "title", "noscript", "meta", "link"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Underline, bool Link)
    {
        public InlineStyle WithBold() => this with { Bold = true };
        public InlineStyle WithItalic() => this with { Italic = true };
        public InlineStyle WithUnderline() => this with { Underline = true };
        public InlineStyle WithLink() => this with { Link = true, Underline = true };
    }

    private readonly record struct InlineRun(string Text, InlineStyle Style);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<RenderHtmlPdfNodeConfiguration>();

        var content = ReadOptionalString(dataContext, config.Path) ?? string.Empty;
        var title = ResolveTitle(dataContext, config);
        var isHtml = ResolveIsHtml(dataContext, config, content);

        byte[] bytes;
        try
        {
            bytes = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(2, Unit.Centimetre);
                        page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

                        if (!string.IsNullOrEmpty(title))
                        {
                            page.Header().Column(header =>
                            {
                                header.Item().Text(title).FontSize(15).Bold();
                                header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                            });
                        }

                        page.Content().PaddingVertical(8).Column(content2 =>
                        {
                            content2.Spacing(6);

                            if (isHtml)
                            {
                                RenderHtml(content, content2);
                            }
                            else
                            {
                                content2.Item().Text(content);
                            }
                        });

                        page.Footer().AlignRight().Text(text =>
                        {
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                    });
                })
                .GeneratePdf();
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.HtmlPdfRenderFailed(nodeContext, ex);
        }

        nodeContext.Debug($"Rendered HTML PDF ({bytes.Length} bytes, html={isHtml})");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(bytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        if (!string.IsNullOrEmpty(config.ContentLengthTargetPath))
        {
            dataContext.Set(config.ContentLengthTargetPath, (long)bytes.Length,
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static void RenderHtml(string html, ColumnDescriptor col)
    {
        var document = new HtmlParser().ParseDocument(html);
        var body = document.Body;
        if (body == null)
        {
            col.Item().Text(html);
            return;
        }

        RenderContainer(body, col, default);
    }

    /// <summary>
    /// Renders the children of <paramref name="container"/> into <paramref name="col"/>,
    /// batching consecutive inline content into a single text item and dispatching
    /// block-level elements to their dedicated renderer.
    /// </summary>
    private static void RenderContainer(INode container, ColumnDescriptor col, InlineStyle style)
    {
        var buffer = new List<InlineRun>();

        void Flush()
        {
            EmitTextItem(buffer, col);
            buffer.Clear();
        }

        foreach (var child in container.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Text:
                    buffer.Add(new InlineRun(Normalize(child.TextContent), style));
                    break;
                case NodeType.Element:
                    var element = (IElement)child;
                    var name = element.LocalName;
                    if (name == "br")
                    {
                        buffer.Add(new InlineRun("\n", style));
                    }
                    else if (InlineTags.Contains(name))
                    {
                        CollectInline(element, style, buffer);
                    }
                    else
                    {
                        Flush();
                        DispatchBlock(element, col, style);
                    }

                    break;
            }
        }

        Flush();
    }

    private static void CollectInline(IElement element, InlineStyle style, List<InlineRun> buffer)
    {
        var name = element.LocalName;
        var childStyle = name switch
        {
            "b" or "strong" => style.WithBold(),
            "i" or "em" or "cite" => style.WithItalic(),
            "u" or "ins" => style.WithUnderline(),
            "a" => style.WithLink(),
            _ => style
        };

        foreach (var child in element.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Text:
                    buffer.Add(new InlineRun(Normalize(child.TextContent), childStyle));
                    break;
                case NodeType.Element:
                    var childElement = (IElement)child;
                    if (childElement.LocalName == "br")
                    {
                        buffer.Add(new InlineRun("\n", childStyle));
                    }
                    else
                    {
                        // Nested elements (inline or the occasional misplaced block) are
                        // flattened to their styled text — good enough inside a text run.
                        CollectInline(childElement, childStyle, buffer);
                    }

                    break;
            }
        }
    }

    private static void DispatchBlock(IElement element, ColumnDescriptor col, InlineStyle style)
    {
        var name = element.LocalName;
        if (SkippedTags.Contains(name))
        {
            return;
        }

        switch (name)
        {
            case "h1": RenderHeading(element, col, 18); break;
            case "h2": RenderHeading(element, col, 16); break;
            case "h3": RenderHeading(element, col, 14); break;
            case "h4": RenderHeading(element, col, 12); break;
            case "h5": RenderHeading(element, col, 11); break;
            case "h6": RenderHeading(element, col, 10); break;
            case "hr":
                col.Item().PaddingVertical(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                break;
            case "ul": RenderList(element, col, style, ordered: false); break;
            case "ol": RenderList(element, col, style, ordered: true); break;
            case "table": RenderTable(element, col, style); break;
            case "pre": RenderPre(element, col); break;
            case "blockquote": RenderBlockquote(element, col, style); break;
            case "img": RenderImage(element, col); break;
            default:
                // div, p, section, tr outside a table, unknown wrappers, … — recurse.
                RenderContainer(element, col, style);
                break;
        }
    }

    private static void RenderHeading(IElement element, ColumnDescriptor col, float fontSize)
    {
        var text = Normalize(element.TextContent).Trim();
        if (text.Length == 0)
        {
            return;
        }

        col.Item().PaddingTop(4).Text(text).FontSize(fontSize).Bold().FontColor(Colors.Grey.Darken3);
    }

    private static void RenderList(IElement element, ColumnDescriptor col, InlineStyle style, bool ordered)
    {
        var index = 1;
        foreach (var item in element.Children)
        {
            if (item.LocalName != "li")
            {
                continue;
            }

            var marker = ordered ? $"{index}." : "•";
            col.Item().PaddingLeft(12).Row(row =>
            {
                row.ConstantItem(18).Text(marker);
                row.RelativeItem().Column(cell => RenderContainer(item, cell, style));
            });
            index++;
        }
    }

    private static void RenderTable(IElement element, ColumnDescriptor col, InlineStyle style)
    {
        var rows = new List<IElement>();
        CollectRows(element, rows);
        if (rows.Count == 0)
        {
            return;
        }

        var grid = rows
            .Select(r => r.Children.Where(c => c.LocalName is "td" or "th").ToList())
            .ToList();
        var columnCount = grid.Max(cells => cells.Count);
        if (columnCount == 0)
        {
            return;
        }

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < columnCount; i++)
                {
                    columns.RelativeColumn();
                }
            });

            foreach (var cells in grid)
            {
                for (var i = 0; i < columnCount; i++)
                {
                    if (i >= cells.Count)
                    {
                        table.Cell().Padding(2).Text(string.Empty);
                        continue;
                    }

                    var cell = cells[i];
                    var cellStyle = cell.LocalName == "th" ? style.WithBold() : style;
                    table.Cell().Padding(2).Column(cellColumn => RenderContainer(cell, cellColumn, cellStyle));
                }
            }
        });
    }

    private static void CollectRows(IElement parent, List<IElement> rows)
    {
        foreach (var child in parent.Children)
        {
            switch (child.LocalName)
            {
                case "tr":
                    rows.Add(child);
                    break;
                case "thead" or "tbody" or "tfoot":
                    CollectRows(child, rows);
                    break;
            }
        }
    }

    private static void RenderPre(IElement element, ColumnDescriptor col)
    {
        col.Item().Background(Colors.Grey.Lighten4).Padding(6)
            .Text(element.TextContent).FontFamily(Fonts.Consolas).FontSize(9);
    }

    private static void RenderBlockquote(IElement element, ColumnDescriptor col, InlineStyle style)
    {
        col.Item().BorderLeft(2).BorderColor(Colors.Grey.Lighten1).PaddingLeft(8)
            .Column(inner => RenderContainer(element, inner, style));
    }

    private static void RenderImage(IElement element, ColumnDescriptor col)
    {
        var src = element.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src) || !TryDecodeDataUri(src, out var bytes))
        {
            // Remote images are not fetched; fall back to the alt text if present.
            var alt = element.GetAttribute("alt");
            if (!string.IsNullOrWhiteSpace(alt))
            {
                col.Item().Text($"[{alt}]").Italic().FontColor(Colors.Grey.Medium);
            }

            return;
        }

        try
        {
            if (TryGetImagePixelWidth(bytes, out var pixelWidth))
            {
                // Map pixels to points at 96 DPI, capped at the content width so a large
                // image fits the page while a small logo keeps roughly its natural size.
                var naturalWidthPt = pixelWidth * 72f / 96f;
                col.Item().Width(Math.Min(naturalWidthPt, ContentWidthPt)).Image(bytes);
            }
            else
            {
                col.Item().MaxWidth(ContentWidthPt).Image(bytes);
            }
        }
        catch
        {
            // Undecodable image payload — skip silently rather than fail the whole receipt.
        }
    }

    /// <summary>
    /// Reads the pixel width from the raw bytes of the common inline-image formats
    /// (PNG, GIF, JPEG, BMP) without an image library. Returns false for anything
    /// else so the caller can fall back to a width-constrained render.
    /// </summary>
    private static bool TryGetImagePixelWidth(byte[] bytes, out int width)
    {
        width = 0;

        // PNG: 8-byte signature, then IHDR with a big-endian width at offset 16.
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            return width > 0;
        }

        // GIF: "GIF8", logical screen width is little-endian at offset 6.
        if (bytes.Length >= 10 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            width = bytes[6] | (bytes[7] << 8);
            return width > 0;
        }

        // BMP: "BM", width is a little-endian int32 at offset 18.
        if (bytes.Length >= 26 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            width = bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24);
            return width > 0;
        }

        // JPEG: FF D8, then walk the marker segments to the first SOF (frame header).
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var pos = 2;
            while (pos + 9 < bytes.Length)
            {
                if (bytes[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }

                var marker = bytes[pos + 1];
                // Start-of-frame markers carry the dimensions; DHT/DAC/RSTn/SOI/EOI do not.
                var isSof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    // FF, marker, length(2), precision(1), height(2), width(2).
                    width = (bytes[pos + 7] << 8) | bytes[pos + 8];
                    return width > 0;
                }

                var segmentLength = (bytes[pos + 2] << 8) | bytes[pos + 3];
                if (segmentLength < 2)
                {
                    return false;
                }

                pos += 2 + segmentLength;
            }
        }

        return false;
    }

    private static bool TryDecodeDataUri(string src, out byte[] bytes)
    {
        bytes = [];
        if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = src.IndexOf(',');
        if (comma < 0 || !src.AsSpan(0, comma).Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(src[(comma + 1)..]);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Emits the buffered inline runs as one text item, collapsing surrounding
    /// whitespace. Whitespace-only buffers (indentation between tags) are dropped.
    /// </summary>
    private static void EmitTextItem(List<InlineRun> runs, ColumnDescriptor col)
    {
        var trimmed = TrimRuns(runs);
        if (trimmed.Count == 0)
        {
            return;
        }

        col.Item().Text(text =>
        {
            foreach (var run in trimmed)
            {
                var span = text.Span(run.Text);
                if (run.Style.Bold)
                {
                    span = span.Bold();
                }

                if (run.Style.Italic)
                {
                    span = span.Italic();
                }

                if (run.Style.Underline)
                {
                    span = span.Underline();
                }

                if (run.Style.Link)
                {
                    span.FontColor(Colors.Blue.Medium);
                }
            }
        });
    }

    private static List<InlineRun> TrimRuns(List<InlineRun> runs)
    {
        var result = runs
            .Where(r => r.Text.Length > 0)
            .ToList();

        // Drop leading / trailing runs that carry no visible text.
        while (result.Count > 0 && result[0].Text.Trim().Length == 0 && result[0].Text != "\n")
        {
            result.RemoveAt(0);
        }

        while (result.Count > 0 && result[^1].Text.Trim().Length == 0 && result[^1].Text != "\n")
        {
            result.RemoveAt(result.Count - 1);
        }

        if (result.All(r => r.Text.Trim().Length == 0 && r.Text != "\n"))
        {
            return [];
        }

        return result;
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(text, " ");

    private static string? ReadOptionalString(IDataContext dataContext, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return dataContext.GetKind(path) == DataKind.String ? dataContext.Get<string>(path) : null;
    }

    private static string? ResolveTitle(IDataContext dataContext, RenderHtmlPdfNodeConfiguration config)
    {
        var fromPath = ReadOptionalString(dataContext, config.TitlePath);
        return !string.IsNullOrWhiteSpace(fromPath) ? fromPath : config.Title;
    }

    private static bool ResolveIsHtml(IDataContext dataContext, RenderHtmlPdfNodeConfiguration config, string content)
    {
        if (!string.IsNullOrEmpty(config.IsHtmlPath) && dataContext.GetKind(config.IsHtmlPath) == DataKind.Boolean)
        {
            return dataContext.Get<bool>(config.IsHtmlPath);
        }

        if (config.IsHtml.HasValue)
        {
            return config.IsHtml.Value;
        }

        // Auto-detect: treat the content as HTML when it contains a tag.
        return content.Contains('<') && content.Contains('>');
    }
}

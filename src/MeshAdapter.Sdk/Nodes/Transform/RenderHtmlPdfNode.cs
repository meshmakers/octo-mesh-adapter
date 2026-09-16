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
// AnyBitmap/Rectangle: cross-platform decode/crop/encode for the tall-image slicing (AB#5259).
// IronSoftware.Drawing comes in with IronOcr, which this assembly already depends on for the
// OCR nodes — no extra native payload is pulled in for it.
using AnyBitmap = IronSoftware.Drawing.AnyBitmap;
using Document = QuestPDF.Fluent.Document;
using IElement = AngleSharp.Dom.IElement;
using Rectangle = IronSoftware.Drawing.Rectangle;

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

    // A4 (841.89pt) minus the 2cm margins, header, footer and paddings. QuestPDF cannot
    // break an image across pages, so an image must fit a single page's content area or
    // the whole document fails with "conflicting size constraints" at layout time.
    private const float ContentHeightPt = 620f;

    // AB#5259: below this rendered width the print of a photographed receipt no longer
    // survives the OCR rasterization (a 542x2573 shop receipt was shrunk to ~131pt ≈ 4.6cm
    // and extracted as completely empty). A too-tall image whose shrink-to-fit width would
    // land below this is sliced across pages instead; above it the plain shrink is kept, so
    // a normal A4 scan that merely overshoots the content height stays a SINGLE page.
    private const float MinShrunkImageWidthPt = 300f;

    // Cap on the pages one image may occupy. An accidental panorama must not turn a single
    // receipt into a hundred-page PDF; past the cap the shrink-to-fit fallback applies. AB#5259.
    private const int MaxImageTiles = 12;

    // Slicing needs the whole bitmap decoded in memory (~4 bytes/pixel). Refuse anything
    // beyond this and fall back to the shrink — the adapter has been OOM-killed by a single
    // poison attachment before (AB#5142) and that must not become possible here. AB#5259.
    private const long MaxDecodablePixels = 40_000_000L;

    // Neighbouring slices overlap by this fraction of a band so a receipt line that falls
    // exactly on a band boundary is still complete in one of the two bands. AB#5259.
    private const float TileOverlapRatio = 0.02f;

    // Upper bound for a page of an image-only document, whose page size follows the image
    // instead of A4. 14400pt (200 inch) is the PDF format's own maximum page edge — beyond
    // it the file would be invalid, so such an image falls back to the A4 render. AB#5259.
    private const float MaxImagePagePt = 14400f;

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

    // Elements that carry no visible content of their own. Only relevant for the
    // image-only detection below: the Stage Document wrapper nests its <img> directly in
    // <body>, but a bare wrapper around it must not change the verdict. AB#5259.
    private static readonly HashSet<string> TransparentWrapperTags =
        ["div", "span", "p", "figure", "center"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Invisible formatting characters: soft hyphen, combining grapheme joiner,
    // zero-width space/non-joiner/joiner, word joiner, zero-width no-break space (BOM).
    // Marketing-mail preheaders pad hundreds of these into hidden text; QuestPDF's
    // text shaper cannot place such runs ("cannot render even a single character")
    // and, depending on the platform's font fallback, either throws a layout
    // exception or allocates until the process is OOM-killed (AB#5142). They carry
    // no visible content, so stripping them is lossless for a rendered receipt.
    [GeneratedRegex("[\\u00AD\\u034F\\u200B-\\u200D\\u2060\\uFEFF]")]
    private static partial Regex InvisibleCharsRegex();

    // Matches a trailing "!important" (with optional inner/outer spacing) on an
    // inline style declaration value.
    [GeneratedRegex(@"!\s*important\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantSuffixRegex();

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

        byte[]? bytes;
        try
        {
            // AB#5259: a document that is nothing but ONE inline image gets pages sized to
            // the image instead of the A4 page layout — see TryRenderImageDocument. The
            // title gate is part of the structural test: a caller that asks for a header
            // (the mail-body render always passes the subject) wants the A4 document, and
            // an image-sized page has nowhere to put a header anyway.
            bytes = isHtml && string.IsNullOrEmpty(title) && TryGetSingleImageBody(content, out var imageBytes)
                ? TryRenderImageDocument(imageBytes)
                : null;

            bytes ??= RenderPagedDocument(content, title, isHtml);
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

    /// <summary>
    /// The general document: A4 with 2cm margins, the optional title as a page header, the
    /// parsed HTML (or plain text) as the content and a page-number footer. This is what a
    /// forwarded e-mail body becomes, and it must stay exactly that — the image-sized
    /// treatment below is deliberately a SEPARATE composition rather than a conditional
    /// page setup here. AB#5259.
    /// </summary>
    private static byte[] RenderPagedDocument(string content, string? title, bool isHtml)
    {
        return Document.Create(container =>
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
                            header.Item().Text(StripInvisible(title)).FontSize(15).Bold();
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
                            content2.Item().Text(StripInvisible(content));
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

    /// <summary>
    /// Renders a document whose whole body is ONE image as the image itself: one page per
    /// band, every page sized exactly to its band, with no margins, header or footer.
    /// <para>
    /// AB#5259 (second attempt, after <c>r3.4.120</c> shipped the tiling alone). Tiling a
    /// too-tall receipt across pages was correct but not sufficient: the bands were still
    /// drawn at <see cref="ContentWidthPt"/> on A4, which places a 542px-wide band on the
    /// page at ~81ppi. Every OCR rasterisation then has to upscale it 2.5-4x and the
    /// interpolation smears the thin thermal-print strokes past recognition. Measured on
    /// the production PDF: the embedded band extracted at its native 542x644px OCRs
    /// perfectly, the A4 page built around it OCRs to NOTHING at 81, 150, 300 and 400 dpi
    /// alike, and cropping the white margins does not help. Sizing the page to the band
    /// keeps 1 image pixel = 1/96 inch, so the rasteriser never has to invent pixels —
    /// with that, tesseract reads the receipt totals back out of the rendered PDF.
    /// </para>
    /// Returns <c>null</c> whenever the image cannot be laid out this way, so the caller
    /// falls back to the A4 render (which skips an unusable image silently).
    /// </summary>
    private static byte[]? TryRenderImageDocument(byte[] imageBytes)
    {
        if (!TryGetImageDimensions(imageBytes, out var pixelWidth, out var pixelHeight))
        {
            // Unknown header format — the A4 path's constrained FitArea render stays in charge.
            return null;
        }

        // The tiling CRITERION is unchanged from the inline path on purpose: exactly the
        // same extreme-aspect images are sliced as before, only the page they land on
        // changes. ContentWidthPt/ContentHeightPt no longer describe the layout box here,
        // they only answer "would this image have had to be shrunk below legibility?".
        var fittedWidthPt = Math.Min(PointsFor(pixelWidth), ContentWidthPt);
        var fittedHeightPt = fittedWidthPt * pixelHeight / pixelWidth;
        var shrunkWidthPt = ContentHeightPt * pixelWidth / (float)pixelHeight;

        List<ImageBand> bands;
        if (fittedHeightPt > ContentHeightPt && shrunkWidthPt < MinShrunkImageWidthPt
                                             && TrySliceIntoBands(imageBytes, pixelWidth, pixelHeight, out var sliced))
        {
            bands = sliced;
        }
        else if (TryGetDecodedSize(imageBytes, pixelWidth, pixelHeight, out var width, out var height))
        {
            // Not extreme enough to slice (or slicing refused by one of its guards): the
            // whole image becomes one page at its natural size.
            bands = [new ImageBand(imageBytes, width, height)];
        }
        else
        {
            // Not decodable within the budget — the page size would be a guess, so leave it
            // to the A4 render, which fits the image into the content box without one.
            return null;
        }

        if (bands.Any(band => PointsFor(band.PixelWidth) > MaxImagePagePt
                              || PointsFor(band.PixelHeight) > MaxImagePagePt))
        {
            return null;
        }

        try
        {
            return Document.Create(container =>
                {
                    foreach (var band in bands)
                    {
                        container.Page(page =>
                        {
                            page.Size(PointsFor(band.PixelWidth), PointsFor(band.PixelHeight), Unit.Point);
                            page.Margin(0);

                            // QuestPDF re-encodes the band at its own compression quality.
                            // It does NOT resample it — a band drawn at its natural size is
                            // 96dpi, far below QuestPDF's 288dpi raster target — so the
                            // pixel grid this fix is about survives. Measured on the prod-1
                            // receipt: re-encoded 191KB and OCR reads "BelegNr: 289943" /
                            // "Brutto 426.00", UseOriginalImage() 2.1MB for the same reading.
                            // Not worth 11x the stored receipt. AB#5259.
                            page.Content().Image(band.Bytes).FitArea();
                        });
                    }
                })
                .GeneratePdf();
        }
        catch
        {
            // A valid header over an undecodable body only fails here, at layout time —
            // fall back to the A4 render rather than failing the whole receipt.
            return null;
        }
    }

    /// <summary>Maps image pixels to PDF points at 96 DPI — the scale the whole node uses.</summary>
    private static float PointsFor(int pixels) => pixels * 72f / 96f;

    /// <summary>
    /// Pixel size read from the DECODED bitmap rather than the header. A page sized to the
    /// image may only ever be built from these: an EXIF-rotated JPEG reports its
    /// pre-rotation dimensions in the SOF marker, and a page cut to those would letterbox
    /// the photo inside white bars — the same reason the slicer recomputes before cropping.
    /// The decodable-pixel budget applies here too (AB#5142): a poison attachment must not
    /// be materialised just to measure it. AB#5259.
    /// </summary>
    private static bool TryGetDecodedSize(byte[] bytes, int pixelWidth, int pixelHeight,
        out int width, out int height)
    {
        width = 0;
        height = 0;

        if ((long)pixelWidth * pixelHeight > MaxDecodablePixels)
        {
            return false;
        }

        try
        {
            using var bitmap = AnyBitmap.FromBytes(bytes);
            width = bitmap.Width;
            height = bitmap.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the document's entire visible content is ONE inline data-URI image and
    /// nothing else — the shape the Stage Document pipeline's image-to-PDF wrapper produces
    /// (<c>&lt;body style="margin:0;padding:0"&gt;&lt;img src="data:…"&gt;&lt;/body&gt;</c>).
    /// The test is structural, on the parsed DOM, so the general HTML path — above all the
    /// e-mail BODY render, which must keep its A4 layout with header, footer and margins —
    /// is never affected. Whitespace and bare wrappers around the image are tolerated; any
    /// text, a second image or any layout element makes it a normal document. AB#5259.
    /// </summary>
    private static bool TryGetSingleImageBody(string html, out byte[] imageBytes)
    {
        imageBytes = [];

        var body = new HtmlParser().ParseDocument(html).Body;
        if (body == null)
        {
            return false;
        }

        IElement? image = null;
        if (!IsImageOnly(body, ref image) || image == null)
        {
            return false;
        }

        var src = image.GetAttribute("src");
        return !string.IsNullOrWhiteSpace(src) && TryDecodeDataUri(src, out imageBytes);
    }

    private static bool IsImageOnly(INode container, ref IElement? image)
    {
        foreach (var child in container.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Text when Normalize(child.TextContent).Trim().Length > 0:
                    return false;
                case NodeType.Element:
                    var element = (IElement)child;
                    var name = element.LocalName;
                    if (SkippedTags.Contains(name))
                    {
                        // Carries no visible content in any case.
                        break;
                    }

                    if (name == "img")
                    {
                        if (image != null)
                        {
                            // A second image is a composed document, not a photo.
                            return false;
                        }

                        image = element;
                        break;
                    }

                    if (!TransparentWrapperTags.Contains(name) || IsHidden(element)
                        || !IsImageOnly(element, ref image))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static void RenderHtml(string html, ColumnDescriptor col)
    {
        var document = new HtmlParser().ParseDocument(html);
        var body = document.Body;
        if (body == null)
        {
            col.Item().Text(StripInvisible(html));
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
        if (IsHidden(element))
        {
            return;
        }

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
        if (SkippedTags.Contains(name) || IsHidden(element))
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
            if (item.LocalName != "li" || IsHidden(item))
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
            .Select(r => r.Children.Where(c => (c.LocalName is "td" or "th") && !IsHidden(c)).ToList())
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
                case "tr" when !IsHidden(child):
                    rows.Add(child);
                    break;
                case "thead" or "tbody" or "tfoot" when !IsHidden(child):
                    CollectRows(child, rows);
                    break;
            }
        }
    }

    private static void RenderPre(IElement element, ColumnDescriptor col)
    {
        col.Item().Background(Colors.Grey.Lighten4).Padding(6)
            .Text(StripInvisible(element.TextContent)).FontFamily(Fonts.Consolas).FontSize(9);
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
                col.Item().Text(StripInvisible($"[{alt}]")).Italic().FontColor(Colors.Grey.Medium);
            }

            return;
        }

        try
        {
            if (TryGetImageDimensions(bytes, out var pixelWidth, out var pixelHeight))
            {
                // Map pixels to points at 96 DPI, capped at the content width so a large
                // image fits the page while a small logo keeps roughly its natural size.
                var widthPt = Math.Min(pixelWidth * 72f / 96f, ContentWidthPt);

                // Cap the resulting height as well: a very tall receipt photo (e.g. a
                // narrow shop receipt) would otherwise exceed a single page.
                var heightPt = widthPt * pixelHeight / pixelWidth;
                if (heightPt > ContentHeightPt)
                {
                    var shrunkWidthPt = ContentHeightPt * pixelWidth / (float)pixelHeight;

                    // AB#5259: shrinking by WIDTH is the only way to fit a tall image on one
                    // page, but for a portrait receipt photo it destroys the document — a
                    // 542x2573 px shop receipt landed ~131pt wide and the OCR/AI stage
                    // extracted nothing at all. Slice it into content-height bands and render
                    // one per page at FULL content width instead. Only the genuinely extreme
                    // aspect ratios go this way; the shrink remains for everything else and
                    // as the fallback whenever the bitmap cannot be sliced.
                    if (shrunkWidthPt < MinShrunkImageWidthPt
                        && TryRenderTiled(bytes, pixelWidth, pixelHeight, col))
                    {
                        return;
                    }

                    widthPt = shrunkWidthPt;
                }

                col.Item().Width(widthPt).Image(bytes);
            }
            else
            {
                // Unknown header format — constrain both axes and let QuestPDF fit the
                // image inside; FitArea may upscale but can never overflow the page.
                col.Item().MaxWidth(ContentWidthPt).MaxHeight(ContentHeightPt).Image(bytes).FitArea();
            }
        }
        catch
        {
            // Undecodable image payload — skip silently rather than fail the whole receipt.
        }
    }

    /// <summary>
    /// Slices a too-tall image into <see cref="ContentHeightPt"/>-sized horizontal bands and
    /// emits one band per page at full content width, INSIDE the A4 document — the shape a
    /// tall image embedded in an e-mail body keeps. An image-only document takes the
    /// natively sized route in <see cref="TryRenderImageDocument"/> instead. Returns
    /// <c>false</c> (and renders nothing) whenever slicing is refused or fails, so the
    /// caller can fall back to the plain shrink-to-fit. AB#5259.
    /// </summary>
    private static bool TryRenderTiled(byte[] bytes, int pixelWidth, int pixelHeight, ColumnDescriptor col)
    {
        if (!TrySliceIntoBands(bytes, pixelWidth, pixelHeight, out var bands))
        {
            return false;
        }

        for (var i = 0; i < bands.Count; i++)
        {
            // One band per page: the bands are sized to the content height, so letting
            // them flow would still fit two partial bands on a page and split the
            // receipt at an arbitrary place a second time.
            if (i > 0)
            {
                col.Item().PageBreak();
            }

            col.Item().Width(ContentWidthPt).Image(bands[i].Bytes);
        }

        return true;
    }

    /// <summary>One horizontal slice of a too-tall image, with its own pixel size. AB#5259.</summary>
    private readonly record struct ImageBand(byte[] Bytes, int PixelWidth, int PixelHeight);

    /// <summary>
    /// Cuts a too-tall image into as many horizontal bands as it needs pages. QuestPDF
    /// cannot break a single image across pages, so the split has to happen on the pixels —
    /// hence the decode/crop/encode round trip. Returns <c>false</c> whenever slicing is
    /// refused by one of its guards or fails, leaving <paramref name="bands"/> empty. AB#5259.
    /// </summary>
    private static bool TrySliceIntoBands(byte[] bytes, int pixelWidth, int pixelHeight,
        out List<ImageBand> bands)
    {
        bands = [];

        // Pre-check on the sniffed header dimensions so an absurd image is rejected BEFORE
        // it is decoded — the point of the guard is not to allocate it in the first place.
        if ((long)pixelWidth * pixelHeight > MaxDecodablePixels)
        {
            return false;
        }

        try
        {
            using var bitmap = AnyBitmap.FromBytes(bytes);

            // Recompute from the decoded bitmap rather than the header: an EXIF-rotated JPEG
            // reports its pre-rotation dimensions in the SOF marker, and cropping with those
            // would slice the wrong axis.
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var fullHeightPt = ContentWidthPt * height / width;
            var tiles = (int)Math.Ceiling(fullHeightPt / ContentHeightPt);
            if (tiles is < 2 or > MaxImageTiles)
            {
                return false;
            }

            // A screenshot (PNG/GIF/BMP) must stay lossless — its text is thin and
            // JPEG ringing is exactly what OCR trips over. A photo is re-encoded as JPEG,
            // where a lossless band would multiply the attachment size for no gain.
            var format = bitmap.GetImageFormat() == AnyBitmap.ImageFormat.Jpeg
                ? AnyBitmap.ImageFormat.Jpeg
                : AnyBitmap.ImageFormat.Png;

            var bandHeight = (int)Math.Ceiling(height / (double)tiles);
            var overlap = (int)(bandHeight * TileOverlapRatio);

            // Encode every band BEFORE handing any of it back: a failure halfway through
            // must leave the caller with nothing, otherwise its fallback render would be
            // appended below a few already-emitted bands.
            var sliced = new List<ImageBand>(tiles);
            for (var i = 0; i < tiles; i++)
            {
                var top = i == 0 ? 0 : Math.Max(0, i * bandHeight - overlap);
                var bottom = Math.Min(height, (i + 1) * bandHeight);
                if (bottom <= top)
                {
                    break;
                }

                using var band = bitmap.Clone(new Rectangle(0, top, width, bottom - top));
                sliced.Add(new ImageBand(band.ExportBytes(format, 95), width, bottom - top));
            }

            if (sliced.Count == 0)
            {
                return false;
            }

            bands = sliced;
            return true;
        }
        catch
        {
            // Undecodable or unsupported payload — the caller falls back to the shrink.
            return false;
        }
    }

    /// <summary>
    /// Reads the pixel dimensions from the raw bytes of the common inline-image formats
    /// (PNG, GIF, JPEG, BMP) without an image library. Returns false for anything
    /// else so the caller can fall back to a constrained render.
    /// </summary>
    private static bool TryGetImageDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        // PNG: 8-byte signature, then IHDR with big-endian width/height at offsets 16/20.
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0;
        }

        // GIF: "GIF8", logical screen width/height are little-endian at offsets 6/8.
        if (bytes.Length >= 10 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            width = bytes[6] | (bytes[7] << 8);
            height = bytes[8] | (bytes[9] << 8);
            return width > 0 && height > 0;
        }

        // BMP: "BM", width/height are little-endian int32s at offsets 18/22 (height may
        // be negative for top-down bitmaps).
        if (bytes.Length >= 26 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            width = bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24);
            height = Math.Abs(bytes[22] | (bytes[23] << 8) | (bytes[24] << 16) | (bytes[25] << 24));
            return width > 0 && height > 0;
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
                    height = (bytes[pos + 5] << 8) | bytes[pos + 6];
                    width = (bytes[pos + 7] << 8) | bytes[pos + 8];
                    return width > 0 && height > 0;
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

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(InvisibleCharsRegex().Replace(text, string.Empty), " ");

    /// <summary>
    /// True when the element's inline style hides it (<c>display:none</c> or
    /// <c>visibility:hidden</c>). Hidden containers — the marketing-mail preheader
    /// pattern — are skipped entirely: their text is invisible in every mail client,
    /// and it is exactly where senders park layout-breaking filler (AB#5142).
    /// The declarations are evaluated with inline-CSS semantics: the last
    /// declaration of a property wins, except that an <c>!important</c> one beats
    /// later non-important ones — so <c>display:none;display:block</c> is visible.
    /// </summary>
    private static bool IsHidden(IElement element)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return false;
        }

        string? display = null;
        var displayImportant = false;
        string? visibility = null;
        var visibilityImportant = false;

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            var important = ImportantSuffixRegex().IsMatch(value);
            if (important)
            {
                value = ImportantSuffixRegex().Replace(value, string.Empty).TrimEnd();
            }

            if (property.Equals("display", StringComparison.OrdinalIgnoreCase))
            {
                if (important || !displayImportant)
                {
                    display = value;
                    displayImportant = important;
                }
            }
            else if (property.Equals("visibility", StringComparison.OrdinalIgnoreCase))
            {
                if (important || !visibilityImportant)
                {
                    visibility = value;
                    visibilityImportant = important;
                }
            }
        }

        return string.Equals(display, "none", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the invisible formatting characters QuestPDF cannot place. Applied at
    /// EVERY text sink — parsed HTML runs go through <see cref="Normalize"/>, while
    /// plain text, titles, <c>&lt;pre&gt;</c> content and image alt fallbacks reach
    /// QuestPDF raw and must be stripped without collapsing their line breaks.
    /// </summary>
    private static string StripInvisible(string text) => InvisibleCharsRegex().Replace(text, string.Empty);

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

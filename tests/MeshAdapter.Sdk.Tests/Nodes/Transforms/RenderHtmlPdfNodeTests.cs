using System.Text;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class RenderHtmlPdfNodeTests : NodeTestBase
{
    // A 1x1 transparent PNG as a data URI — exercises the inline-image path.
    private const string PngDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M8AAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    private static void AssertIsPdf(string? base64)
    {
        Assert.NotNull(base64);
        var bytes = Convert.FromBase64String(base64!);
        Assert.True(bytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    /// <summary>
    /// Extracts the text of every page so tests can assert the rendered-content
    /// contract — visible text present, hidden/skipped text absent — rather than
    /// only the PDF header.
    /// </summary>
    private static string ExtractPdfText(string? base64)
    {
        Assert.NotNull(base64);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Convert.FromBase64String(base64));
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }

    [Fact]
    public async Task ProcessObjectAsync_RichHtml_RendersPdf()
    {
        const string html = """
            <html><body>
              <h1>Invoice INV-2025-042</h1>
              <p>Dear customer, <b>thank you</b> for your <i>order</i>.</p>
              <ul><li>Item A</li><li>Item B</li></ul>
              <table>
                <tr><th>Position</th><th>Amount</th></tr>
                <tr><td>Consulting</td><td>1.200,00 EUR</td></tr>
              </table>
              <blockquote>Please pay within 14 days.</blockquote>
              <p><a href="https://example.com">View online</a></p>
            </body></html>
            """;
        var config = new RenderHtmlPdfNodeConfiguration
            { Path = "$.html", TargetPath = "$.pdf", Title = "Forwarded mail", ContentLengthTargetPath = "$.pdfLen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var base64 = CapturedString(dataContext, config.TargetPath);
        AssertIsPdf(base64);
        var len = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == "$.pdfLen").Arguments[1];
        Assert.Equal((long)Convert.FromBase64String(base64!).Length, len);
    }

    [Fact]
    public async Task ProcessObjectAsync_InlineDataUriImage_RendersPdf()
    {
        var html = $"<p>Logo:</p><img src=\"{PngDataUri}\" alt=\"logo\"/><p>End.</p>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    /// <summary>
    /// Builds a real bitmap of exactly the requested pixel size. QuestPDF's
    /// <c>Placeholders.Image(w, h)</c> only honours the ASPECT RATIO — it returns a 13x64
    /// thumbnail for a 542x2573 request — so it can never reach the size-dependent branches
    /// of <see cref="RenderHtmlPdfNode"/> and is useless for the tiling tests. AB#5259.
    /// </summary>
    private static byte[] CreateImage(int pixelWidth, int pixelHeight,
        IronSoftware.Drawing.AnyBitmap.ImageFormat format = IronSoftware.Drawing.AnyBitmap.ImageFormat.Jpeg)
    {
        using var bitmap = new IronSoftware.Drawing.AnyBitmap(
            pixelWidth, pixelHeight, IronSoftware.Drawing.Color.White);
        return bitmap.ExportBytes(format, 90);
    }

    /// <summary>
    /// A payload with a valid JPEG header but a garbage body: it passes the dimension
    /// sniffer (SOF0 claims 2570 x 210, whose shrunk width would be ~51pt, so the tall-image
    /// branch is taken) and then fails to decode. AB#5259.
    /// </summary>
    private static readonly byte[] BrokenTallJpeg =
    [
        0xFF, 0xD8, // SOI
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0A, 0x0A, 0x00, 0xD2, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01,
        0x03, 0x11, 0x01, // SOF0: 2570 x 210
        0x00, 0x00, 0x00, 0x00
    ];

    private static int PageCount(string? base64)
    {
        Assert.NotNull(base64);
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(
            new MemoryStream(Convert.FromBase64String(base64!)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return pdf.PageCount;
    }

    /// <summary>
    /// Page sizes in POINTS, one entry per page. AB#5259: the image-only document sizes its
    /// pages to the image, so the page geometry — not only the page count — is part of the
    /// contract now.
    /// </summary>
    private static IReadOnlyList<(double Width, double Height)> PageSizes(string? base64)
    {
        Assert.NotNull(base64);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Convert.FromBase64String(base64!));
        return pdf.GetPages().Select(p => (p.Width, p.Height)).ToList();
    }

    private const double A4WidthPt = 595d;
    private const double A4HeightPt = 842d;

    private static void AssertAllPagesA4(string? base64)
    {
        Assert.All(PageSizes(base64), size =>
        {
            Assert.Equal(A4WidthPt, size.Width, 1d);
            Assert.Equal(A4HeightPt, size.Height, 1d);
        });
    }

    /// <summary>
    /// The e-mail shape: an image embedded in a mail BODY, alongside text. Stays on the
    /// A4 document with header, margins and page-number footer.
    /// </summary>
    private async Task<string?> RenderImageHtmlAsync(byte[] jpeg, string? title = null)
    {
        var html = $"<p>Von meinem iPhone gesendet</p><img src=\"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}\"/>";
        return await RenderAsync(html, title);
    }

    /// <summary>
    /// The Stage Document shape: the pipeline's image-to-PDF wrapper, verbatim — a body
    /// whose entire content is one data-URI image and no title. AB#5259.
    /// </summary>
    private async Task<string?> RenderImageDocumentAsync(byte[] image, string contentType = "image/jpeg")
    {
        var html = "<!DOCTYPE html><html><body style=\"margin:0;padding:0\">"
                   + $"<img src=\"data:{contentType};base64,{Convert.ToBase64String(image)}\" "
                   + "style=\"display:block;width:100%;height:auto\"></body></html>";
        return await RenderAsync(html, title: null);
    }

    private async Task<string?> RenderAsync(string html, string? title)
    {
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf", Title = title };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        await new RenderHtmlPdfNode(next).ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var base64 = CapturedString(dataContext, config.TargetPath);
        AssertIsPdf(base64);
        return base64;
    }

    [Fact]
    public async Task ProcessObjectAsync_VeryTallImageDocument_IsTiledOnBandSizedPages()
    {
        // AB#5259, both attempts in one test. A narrow, very tall receipt photo (prod-1
        // evidence: a 542x2573 px Stadtgemeinde Salzburg shop receipt) used to be shrunk by
        // WIDTH until its full height fitted ONE page — it landed ~131pt (4.6cm) wide on A4
        // and the OCR/AI stage extracted nothing at all from it.
        //
        // The first fix sliced it into 4 bands, one per page — correct, and still the
        // measured result in production was an empty extraction, because the bands were
        // drawn at 480pt on A4: a 542px band at 480pt sits in the page at ~81ppi, and every
        // OCR rasterisation of it upscales 2.5-4x and smears the thermal print away. So the
        // PAGE has to follow the band: 542px at 96dpi = 406.5pt wide, no margins, no footer.
        var base64 = await RenderImageDocumentAsync(CreateImage(542, 2573));

        var sizes = PageSizes(base64);
        Assert.True(sizes.Count >= 4, $"expected at least 4 band pages, got {sizes.Count}");
        Assert.All(sizes, size =>
        {
            // Natural width — NOT A4, and not the 480pt content width of the first attempt.
            Assert.Equal(542 * 72d / 96d, size.Width, 1d);

            // Every band is a slice of the photo, so no page may be taller than the whole
            // image and none may carry A4's letterbox of white margin.
            Assert.InRange(size.Height, 1d, 2573 * 72d / 96d);
        });

        // The band heights must add up to the image (plus the deliberate slice overlap):
        // no band may have been dropped.
        Assert.True(sizes.Sum(s => s.Height) >= 2573 * 72d / 96d,
            $"bands cover only {sizes.Sum(s => s.Height)}pt of a {2573 * 72d / 96d}pt image");
    }

    [Fact]
    public async Task ProcessObjectAsync_VeryTallInlineImageInMailBody_IsTiledOnA4Pages()
    {
        // The same photo INSIDE a mail body (text above it) is not an image document: it
        // keeps the A4 layout and is tiled at the 480pt content width, exactly as before.
        // 480pt * 2573/542 = 2279pt of image over a 620pt content height = 4 bands. AB#5259.
        var base64 = await RenderImageHtmlAsync(CreateImage(542, 2573));

        Assert.True(PageCount(base64) >= 4,
            $"expected the tall receipt to be tiled over at least 4 pages, got {PageCount(base64)}");
        AssertAllPagesA4(base64);
    }

    [Fact]
    public async Task ProcessObjectAsync_NormalAspectImageDocument_UsesOneImageSizedPage()
    {
        // An image document that needs no slicing still gets a page of its own size — one
        // pixel stays one 96th of an inch, which is the whole point of the change. AB#5259.
        var base64 = await RenderImageDocumentAsync(CreateImage(1600, 2200));

        var sizes = PageSizes(base64);
        Assert.Single(sizes);
        Assert.Equal(1600 * 72d / 96d, sizes[0].Width, 1d);
        Assert.Equal(2200 * 72d / 96d, sizes[0].Height, 1d);
    }

    [Fact]
    public async Task ProcessObjectAsync_UndecodableImageDocument_FallsBackToA4()
    {
        // A valid JPEG header over a garbage body passes the dimension sniffer but cannot
        // be decoded — and a page may only ever be cut from DECODED dimensions, because an
        // EXIF-rotated JPEG lies about its size in the SOF marker. The image document must
        // therefore fall back to the A4 render, which drops the unusable image rather than
        // failing the receipt — never to a page sized from the lie. AB#5259.
        var base64 = await RenderImageDocumentAsync(BrokenTallJpeg);

        Assert.Equal(1, PageCount(base64));
        AssertAllPagesA4(base64);
    }

    [Fact]
    public async Task ProcessObjectAsync_MailBodyWithTitle_StaysOnA4()
    {
        // The regression guard for the other consumer of this node: the no-attachment
        // branch of the e-mail import renders the mail BODY into the receipt PDF and must
        // keep the A4 page with its header, margins and page-number footer. AB#5259.
        const string html = """
            <html><body>
              <h1>Rechnung 2026-042</h1>
              <p>Betrag: 426,00 EUR</p>
              <table><tr><th>Position</th><th>Betrag</th></tr><tr><td>Schutt</td><td>426,00</td></tr></table>
            </body></html>
            """;

        var base64 = await RenderAsync(html, title: "Fwd: Beleg Wirtschaftshof");

        AssertAllPagesA4(base64);
        var text = ExtractPdfText(base64);
        Assert.Contains("Fwd: Beleg Wirtschaftshof", text);
        Assert.Contains("426,00", text);
        // The page-number footer belongs to the A4 document and must still be there.
        Assert.Contains("1 / 1", text);
    }

    [Fact]
    public async Task ProcessObjectAsync_NormalAspectInlineImage_StaysOnOnePage()
    {
        // The counterpart to the tiling above: an ordinary portrait scan overshoots the
        // content height only slightly (480pt * 2200/1600 = 660pt vs. 620pt) and is still
        // perfectly legible after the shrink-to-fit — 451pt wide. Splitting THAT across two
        // pages would be a regression, so only the extreme aspect ratios are tiled.
        var base64 = await RenderImageHtmlAsync(CreateImage(1600, 2200));

        Assert.Equal(1, PageCount(base64));
    }

    [Fact]
    public async Task ProcessObjectAsync_SmallInlineImage_StaysOnOnePage()
    {
        // A logo-sized image keeps its natural size and never goes near the tiling path.
        var base64 = await RenderImageHtmlAsync(CreateImage(200, 80));

        Assert.Equal(1, PageCount(base64));
    }

    [Fact]
    public async Task ProcessObjectAsync_UndecodableTallImage_FallsBackToShrunkSinglePage()
    {
        // The shrink-to-fit stays the fallback whenever the bitmap cannot be sliced. A
        // payload with a valid JPEG header but a garbage body passes the dimension sniffer
        // (so the tall-image branch is taken) and then fails to decode — the node must fall
        // back to the old single-page render instead of dropping the image or throwing. AB#5259.
        var base64 = await RenderImageHtmlAsync(BrokenTallJpeg);

        Assert.Equal(1, PageCount(base64));
    }

    [Fact]
    public async Task ProcessObjectAsync_PlainText_RendersPdf()
    {
        const string text = "Simple forwarded note\nSecond line\nThird line";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.body", TargetPath = "$.pdf", IsHtml = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.body")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.body")).Returns(text);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_HiddenPreheaderWithInvisibleCharacters_RendersPdf()
    {
        // Real-world poison mail (AB#5142, prod-1 OOM loop): a marketing preheader
        // hidden via display:none, padded with hundreds of COMBINING GRAPHEME JOINER
        // (U+034F) and SOFT HYPHEN (U+00AD) characters. QuestPDF cannot lay out such
        // a run — depending on the platform's fonts it either throws a layout
        // exception or allocates until the process is OOM-killed.
        var preheader = "Logge Dich in Deinem Konto ein, um zu bezahlen "
                        + string.Concat(Enumerable.Repeat("\u034F  ", 150))
                        + string.Concat(Enumerable.Repeat("\u00AD ", 150));
        var html = "<table><tbody>"
                   + $"<tr><td><div style=\"display:none\">{preheader}</div></td></tr>"
                   + "<tr><td>Bitte zahle den offenen Betrag: 64,48 EUR</td></tr>"
                   + "</tbody></table>";
        var config = new RenderHtmlPdfNodeConfiguration
            { Path = "$.html", TargetPath = "$.pdf", Title = "Fwd: Achtung: Deine Zahlung ist fehlgeschlagen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("64,48", text);
        Assert.DoesNotContain("Logge Dich", text);
    }

    [Fact]
    public async Task ProcessObjectAsync_VisibleInvisibleCharacterRun_IsStrippedAndRendersPdf()
    {
        // The same invisible-character padding OUTSIDE a hidden container — the
        // characters carry no visible content and are stripped during normalization,
        // so the layout never sees the unplaceable run.
        var html = "<p>Betrag fällig"
                   + string.Concat(Enumerable.Repeat("\u034F \u00AD\u200B\u200D\u2060\uFEFF", 100))
                   + "am 08.09.2026</p>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("Betrag fällig", text);
        Assert.Contains("am 08.09.2026", text);
        Assert.DoesNotContain('\u00AD', text);
        Assert.DoesNotContain('\u034F', text);
    }

    [Fact]
    public async Task ProcessObjectAsync_HiddenPreheaderWithImportantVariant_RendersPdf()
    {
        // Mail HTML overwhelmingly writes "display:none!important" (no space, with
        // !important) — the hidden-style detection must match these variants too.
        var preheader = "PREHEADER1 " + string.Concat(Enumerable.Repeat("\u034F  ", 200));
        var html = $"<div style=\"display:none!important;\">{preheader}</div>"
                   + $"<div style=\"display: none !important\">PREHEADER2</div>"
                   + "<p>Sichtbarer Beleginhalt</p>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_HiddenInlineElement_IsSkipped()
    {
        // visibility:hidden on an inline element inside a text run.
        var html = "<p>Visible<span style=\"visibility: hidden\">HIDDENSENTINEL"
                   + string.Concat(Enumerable.Repeat("\u034F ", 200))
                   + "</span> text</p>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("Visible", text);
        Assert.Contains("text", text);
        Assert.DoesNotContain("HIDDENSENTINEL", text);
    }

    [Fact]
    public async Task ProcessObjectAsync_PlainTextWithInvisibleCharacterRun_IsStrippedAndKeepsLineBreaks()
    {
        // The plain-text sink bypasses Normalize — the invisible characters must be
        // stripped there too, WITHOUT collapsing the line breaks plain text relies on.
        var text = "Zeile eins\n"
                   + string.Concat(Enumerable.Repeat("\u034F \u00AD", 200))
                   + "\nZeile zwei";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.body", TargetPath = "$.pdf", IsHtml = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.body")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.body")).Returns(text);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var rendered = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("Zeile eins", rendered);
        Assert.Contains("Zeile zwei", rendered);
        Assert.DoesNotContain('\u00AD', rendered);
        Assert.DoesNotContain('\u034F', rendered);
    }

    [Fact]
    public async Task ProcessObjectAsync_PreWithInvisibleCharacterRun_IsStripped()
    {
        var html = "<pre>Code A"
                   + string.Concat(Enumerable.Repeat("\u034F\u00AD", 200))
                   + "Code B</pre>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var rendered = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("Code A", rendered);
        Assert.Contains("Code B", rendered);
        Assert.DoesNotContain('\u00AD', rendered);
        Assert.DoesNotContain('\u034F', rendered);
    }

    [Fact]
    public async Task ProcessObjectAsync_OverriddenHiddenDeclaration_StaysVisible()
    {
        // Inline-CSS semantics: the LAST declaration of a property wins, unless an
        // earlier one is !important — display:none;display:block is visible.
        const string html = "<div style=\"display:none;display:block\">VISIBLEOVERRIDE</div>"
                            + "<div style=\"display:block;display:none\">HIDDENLAST</div>"
                            + "<div style=\"display:none!important;display:block\">HIDDENIMPORTANT</div>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("VISIBLEOVERRIDE", text);
        Assert.DoesNotContain("HIDDENLAST", text);
        Assert.DoesNotContain("HIDDENIMPORTANT", text);
    }

    [Fact]
    public async Task ProcessObjectAsync_HiddenTableSection_IsSkipped()
    {
        // A hidden <tbody>/<thead> must not contribute rows \u2014 only checking the
        // <tr> elements would still render their content.
        const string html = "<table>"
                            + "<thead style=\"display:none\"><tr><td>HIDDENHEAD</td></tr></thead>"
                            + "<tbody style=\"display:none!important\"><tr><td>HIDDENBODY</td></tr></tbody>"
                            + "<tbody><tr><td>Sichtbare Zeile</td></tr></tbody>"
                            + "</table>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = ExtractPdfText(CapturedString(dataContext, config.TargetPath));
        Assert.Contains("Sichtbare Zeile", text);
        Assert.DoesNotContain("HIDDENHEAD", text);
        Assert.DoesNotContain("HIDDENBODY", text);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyContent_RendersPdf()
    {
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // GetKind returns Undefined (default) → content resolves to empty string.

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }
}

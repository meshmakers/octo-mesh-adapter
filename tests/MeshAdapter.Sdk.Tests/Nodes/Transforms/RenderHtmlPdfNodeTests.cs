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

    [Fact]
    public async Task ProcessObjectAsync_VeryTallInlineImage_RendersSinglePagePdf()
    {
        // A narrow, very tall receipt photo (real-world case: 484x2016 iPhone shot of a
        // long shop receipt). Width-only capping made the layout exceed one page and
        // QuestPDF failed with "conflicting size constraints".
        var jpeg = QuestPDF.Helpers.Placeholders.Image(484, 2016);
        var html = $"<p>Von meinem iPhone gesendet</p><img src=\"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}\"/>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf", Title = "Wirtschaftsforum Rechnung" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var base64 = CapturedString(dataContext, config.TargetPath);
        AssertIsPdf(base64);
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(
            new MemoryStream(Convert.FromBase64String(base64!)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
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
        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
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
        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_HiddenPreheaderWithImportantVariant_RendersPdf()
    {
        // Mail HTML overwhelmingly writes "display:none!important" (no space, with
        // !important) — the hidden-style detection must match these variants too.
        var preheader = string.Concat(Enumerable.Repeat("\u034F  ", 200));
        var html = $"<div style=\"display:none!important;\">{preheader}</div>"
                   + $"<div style=\"display: none !important\">{preheader}</div>"
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
        var html = "<p>Visible<span style=\"visibility: hidden\">"
                   + string.Concat(Enumerable.Repeat("\u034F ", 200))
                   + "</span> text</p>";
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

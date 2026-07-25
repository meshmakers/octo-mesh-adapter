using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Covers the embedded text-layer extraction path added in AB#4528. A digital PDF
/// carries an exact text layer; the node must use it instead of raster+Tesseract OCR,
/// which drops separator-less alphanumeric codes such as invoice numbers. The fixture
/// PDF is produced by <see cref="RenderHtmlPdfNode"/> (real embedded text, not a scan).
/// </summary>
public class PdfOcrExtractionNodeTests : NodeTestBase
{
    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    private static object? CapturedValue(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1];
    }

    /// <summary>
    /// Renders plain text into a base64 digital PDF via <see cref="RenderHtmlPdfNode"/>,
    /// giving us a fixture whose text layer PdfPig can read — no scanning, no OCR needed.
    /// </summary>
    private async Task<string> RenderTextPdfAsync(string text)
    {
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.body", TargetPath = "$.pdf", IsHtml = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.body")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.body")).Returns(text);

        await new RenderHtmlPdfNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var base64 = CapturedString(dataContext, "$.pdf");
        Assert.NotNull(base64);
        return base64!;
    }

    [Fact]
    public async Task ProcessObjectAsync_DigitalPdf_ExtractsInvoiceNumberFromTextLayer()
    {
        // "N2026020" is exactly the kind of separator-less token Tesseract loses but the
        // text layer preserves verbatim. (Diacritic fidelity is covered against a real
        // Fakturownia PDF, not this fixture — the QuestPDF fixture font flattens ł/ó/ś.)
        // The body must exceed MinTextLayerChars (100) so the digital-PDF gate engages.
        var pdf = await RenderTextPdfAsync(
            "Rechnung Nr. N2026020\n" +
            "Erstellungsdatum: 30.05.2026, Verkaufsdatum: 30.05.2026\n" +
            "Position 1: Auftragsentwicklung 160 Std zu 30,00 EUR = 4.800,00 EUR\n" +
            "Position 2: Auftragsentwicklung 160 Std zu 38,00 EUR = 6.080,00 EUR\n" +
            "Gesamtbetrag 10.880,00 EUR, Steuerschuldnerschaft des Leistungsempfaengers");

        var config = new PdfOcrExtractionNodeConfiguration
            { Path = "$.pdf", TargetPath = "$.text", Language = "de", IncludeConfidence = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<string>("$.pdf")).Returns(pdf);

        await new PdfOcrExtractionNode(next).ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = CapturedString(dataContext, "$.text");
        Assert.NotNull(text);
        Assert.Contains("N2026020", text);
        // Text-layer path stamps a deterministic confidence of 100 (not an OCR estimate).
        Assert.Equal(100d, CapturedValue(dataContext, "$.Confidence"));
    }
}

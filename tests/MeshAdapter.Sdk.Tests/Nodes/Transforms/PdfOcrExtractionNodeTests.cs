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

    [Fact]
    public async Task ProcessObjectAsync_PdfWithArchivePreamble_IsTreatedAsPdfNotImage()
    {
        // Invoice-archive systems (e.g. UTA's "Transform Foundation Server") deliver PDFs
        // with a proprietary metadata preamble before the %PDF header. The PDF spec grants
        // readers a 1024-byte tolerance for locating the header; classifying such a file
        // as an image sends it down the OcrImageInput path, which fails. AB#4533.
        var pdf = await RenderTextPdfAsync(
            "Gesamtsummenblatt (Nicht gueltig fuer Umsatzsteuerzwecke)\n" +
            "Abrechnungs-Nr. 56719006, Abrechnungsdatum 15.12.2025\n" +
            "Gesamtbetrag exkl. USt 158,76 EUR, USt 31,75 EUR\n" +
            "Gesamtbetrag inkl. USt 190,51 EUR");

        var preamble =
            "%%_Typ|ArchivCM\r\n%%_Server|vTrans-3\r\n%%_Seiten|1\r\n" +
            "%%_dokType|KUNDENABRECHNUNG\r\n%%_Format|PDF\r\n%%_Art|Original\r\n";
        var wrapped = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes(preamble)
                .Concat(Convert.FromBase64String(pdf))
                .ToArray());

        var config = new PdfOcrExtractionNodeConfiguration
            { Path = "$.pdf", TargetPath = "$.text", Language = "de", IncludeConfidence = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<string>("$.pdf")).Returns(wrapped);

        await new PdfOcrExtractionNode(next).ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var text = CapturedString(dataContext, "$.text");
        Assert.NotNull(text);
        Assert.Contains("56719006", text);
        // Confidence 100 proves the text-layer path ran, i.e. the file was seen as a PDF.
        Assert.Equal(100d, CapturedValue(dataContext, "$.Confidence"));
    }

    // --- AB#5259: hybrid PDFs (text layer for the labels, figures as embedded images) ----
    // The decision and the merge are pure functions so they can be asserted without paying
    // for a full Tesseract run; the end-to-end contract of the untouched digital-PDF path
    // (text layer wins, OCR skipped, confidence 100) is covered by the two tests above.

    [Fact]
    public void IsTextLayerStructurallyComplete_HybridInvoice_ReportsTheLabelWithoutAnAmount()
    {
        // Verbatim shape of the prod-1 evidence (uploaded documents 6aa9a681…43c3 / …43c7,
        // "26034 Lehrlingstraining"): pdftotext -layout returns the labels and NOT ONE of the
        // three amounts, because the amount column is one of 43 embedded stencil images.
        const string textLayer = """
            Potenzialwerkstatt Mag. Halina Gruber
            Rechnung 26034 vom 12.09.2026
            Leistungsbetrag gesamt
            MWSt 20%
            Bruttosumme
            Zahlbar innerhalb von 14 Tagen ohne Abzug.
            """;

        Assert.False(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, null, out var label));
        Assert.NotNull(label);
    }

    [Fact]
    public void IsTextLayerStructurallyComplete_DigitalInvoiceWithAmounts_IsComplete()
    {
        // The AB#4528 case must keep the cheap text-layer path: every total label has its
        // figure right next to it, so there is nothing for OCR to add.
        const string textLayer = """
            Rechnung Nr. N2026020 vom 30.05.2026
            Position 1: Auftragsentwicklung 160 Std zu 30,00 EUR = 4.800,00 EUR
            Zwischensumme 4.800,00 EUR
            MWSt 20% 960,00 EUR
            Gesamtbetrag 5.760,00 EUR
            """;

        Assert.True(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, null, out var label));
        Assert.Null(label);
    }

    [Fact]
    public void IsTextLayerStructurallyComplete_DocumentWithoutAmountLabels_IsNotJudged()
    {
        // An info sheet or an attendance confirmation carries no total at all. It must NOT be
        // dragged through the OCR merge — there is no figure to recover, only cost.
        const string textLayer = """
            Teilnahmebestaetigung
            Hiermit bestaetigen wir die Teilnahme am Lehrlingstraining am 12.09.2026.
            Die Veranstaltung umfasste 8 Einheiten zu je 50 Minuten.
            """;

        Assert.True(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, null, out _));
    }

    [Fact]
    public void IsTextLayerStructurallyComplete_DateNextToALabel_DoesNotCountAsAnAmount()
    {
        // "30.05.2026" must not satisfy a total label — a date-shaped token next to
        // "Gesamtbetrag" would otherwise mask exactly the defect this check exists for.
        const string textLayer = """
            Leistungszeitraum bis 30.05.2026
            Gesamtbetrag
            Wir danken fuer Ihren Auftrag.
            """;

        Assert.False(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, null, out _));
    }

    [Fact]
    public void IsTextLayerStructurallyComplete_CustomAmountLabels_OverrideTheBuiltInSet()
    {
        const string textLayer = """
            Consumption report
            Reading 2026-09-12
            Meter value
            """;

        Assert.False(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, ["meter value"], out var label));
        Assert.Equal("meter value", label);
        // The built-in invoice vocabulary does not apply to this document at all.
        Assert.True(PdfOcrExtractionNode.IsTextLayerStructurallyComplete(textLayer, null, out _));
    }

    [Fact]
    public void MergeTextLayerWithOcr_KeepsTheTextLayerVerbatimAndAppendsOnlyNewLines()
    {
        // The text layer stays authoritative: "N2026020" (the separator-less invoice number
        // Tesseract mangles into "NZ02G020") must survive unchanged, the label lines must not
        // be duplicated in their OCR form, and the image-only amounts must be added.
        const string textLayer = """
            Rechnung Nr. N2026020
            Leistungsbetrag gesamt
            Bruttosumme
            """;
        const string ocrText = """
            Rechnung Nr. NZ02G020
            Leistungsbetrag gesamt 520,00
            Bruttosumme
            624,00 EUR
            """;

        var merged = PdfOcrExtractionNode.MergeTextLayerWithOcr(textLayer, ocrText);

        Assert.StartsWith(textLayer, merged);
        Assert.Contains(PdfOcrExtractionNode.OcrSupplementMarker, merged);
        Assert.Contains("520,00", merged);
        Assert.Contains("624,00 EUR", merged);
        // "Bruttosumme" adds no token the text layer does not have -> not repeated.
        Assert.Equal(1, merged.Split("Bruttosumme").Length - 1);
    }

    [Fact]
    public void MergeTextLayerWithOcr_OcrAddsNothingNew_ReturnsTheTextLayerUnchanged()
    {
        const string textLayer = "Gesamtbetrag 5.760,00 EUR\nMWSt 20% 960,00 EUR";

        Assert.Equal(textLayer, PdfOcrExtractionNode.MergeTextLayerWithOcr(textLayer, "Gesamtbetrag 5.760,00 EUR"));
        Assert.Equal(textLayer, PdfOcrExtractionNode.MergeTextLayerWithOcr(textLayer, "   \n  \n"));
        Assert.Equal(textLayer, PdfOcrExtractionNode.MergeTextLayerWithOcr(textLayer, string.Empty));
    }
}

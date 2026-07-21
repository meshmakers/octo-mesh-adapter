using Meshmakers.Octo.Sdk.MeshAdapter.Services.Pdf;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MeshAdapter.Sdk.Tests.Services.Pdf;

/// <summary>
/// Tests the PdfPig-based text-layer extraction (AB#4464) against a born-digital PDF
/// generated in-test with QuestPDF — no OCR, no external fixtures.
/// </summary>
public class PdfPigTextExtractorTests
{
    private const string Marker = "KnowledgeCapture text layer marker 1234567890";

    private static byte[] CreateBornDigitalPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(20);
                page.Content().Text(Marker);
            });
        }).GeneratePdf();
    }

    [Fact]
    public void Extract_BornDigitalPdf_ReadsTextLayerLosslessly()
    {
        var pdfBytes = CreateBornDigitalPdf();

        var result = new PdfPigTextExtractor().Extract(pdfBytes, textLayerMinChars: 10);

        var page = Assert.Single(result.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.True(page.HasTextLayer);
        // Born-digital text page without a dominating image: NOT text-on-image.
        Assert.False(page.IsTextOnImage);
        // Lossless: the marker must come back exactly (OCR could not guarantee this).
        Assert.Contains(Marker, page.Text);
    }

    [Fact]
    public void Extract_ThresholdAbovePageContent_ReportsNoUsableTextLayer()
    {
        var pdfBytes = CreateBornDigitalPdf();

        // Threshold larger than the page's content — the page must NOT count as having
        // a usable text layer (guards against stray-watermark "text layers").
        var result = new PdfPigTextExtractor().Extract(pdfBytes, textLayerMinChars: 10_000);

        var page = Assert.Single(result.Pages);
        Assert.False(page.HasTextLayer);
    }

    [Fact]
    public void Extract_PdfWithoutAttachments_ReturnsNoEmbeddedFiles()
    {
        var pdfBytes = CreateBornDigitalPdf();

        var result = new PdfPigTextExtractor().Extract(pdfBytes, textLayerMinChars: 10);

        Assert.Empty(result.EmbeddedFiles);
    }
}

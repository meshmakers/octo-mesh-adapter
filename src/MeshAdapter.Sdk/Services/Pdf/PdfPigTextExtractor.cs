using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.Pdf;

/// <summary>
///     <see cref="IPdfTextExtractor"/> implementation based on PdfPig (Apache-2.0,
///     pure managed). Uses <see cref="ContentOrderTextExtractor"/> so text comes back
///     in reading order rather than raw content-stream order.
/// </summary>
public class PdfPigTextExtractor : IPdfTextExtractor
{
    /// <inheritdoc />
    public PdfTextExtractionResult Extract(byte[] pdfBytes, int textLayerMinChars)
    {
        var pages = new List<PdfPageText>();
        var embeddedFiles = new List<PdfEmbeddedFile>();

        using var document = PdfDocument.Open(pdfBytes);

        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            var hasTextLayer = !string.IsNullOrWhiteSpace(text) &&
                               CountNonWhitespace(text) >= textLayerMinChars;
            pages.Add(new PdfPageText(page.Number, text, hasTextLayer));
        }

        if (document.Advanced.TryGetEmbeddedFiles(out var files) && files is not null)
        {
            foreach (var file in files)
            {
                embeddedFiles.Add(new PdfEmbeddedFile(file.Name, file.Bytes.ToArray()));
            }
        }

        return new PdfTextExtractionResult(pages, embeddedFiles);
    }

    private static int CountNonWhitespace(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                count++;
            }
        }

        return count;
    }
}

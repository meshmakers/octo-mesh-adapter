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
    /// <summary>
    ///     A single image covering at least this fraction of the page area marks the page as
    ///     image-dominated (scan). Together with a present text layer that is the
    ///     "text-on-image" pattern: the text layer stems from a previous OCR pass.
    /// </summary>
    private const double FullPageImageAreaRatio = 0.7;

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
            var isTextOnImage = hasTextLayer && HasFullPageImage(page);
            pages.Add(new PdfPageText(page.Number, text, hasTextLayer, isTextOnImage));
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

    private static bool HasFullPageImage(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            var pageArea = (double)(page.Width * page.Height);
            if (pageArea <= 0)
            {
                return false;
            }

            foreach (var image in page.GetImages())
            {
                var bounds = image.BoundingBox;
                if (bounds.Width * bounds.Height >= FullPageImageAreaRatio * pageArea)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Image enumeration is a heuristic; exotic image encodings must not break
            // text extraction. Unknown => not flagged as text-on-image.
        }

        return false;
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

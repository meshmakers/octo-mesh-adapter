namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.Pdf;

/// <summary>
///     Text of a single PDF page as read from the embedded text layer (no OCR).
/// </summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="Text">The text-layer content of the page (may be empty).</param>
/// <param name="HasTextLayer">
///     True when the page carries a usable embedded text layer (at least the configured
///     minimum number of non-whitespace characters). Pages without one (scans, pure
///     image pages) need OCR.
/// </param>
public record PdfPageText(int PageNumber, string Text, bool HasTextLayer);

/// <summary>
///     A file embedded in the PDF (PDF file attachment), e.g. the structured e-invoice XML
///     carried by ZUGFeRD / Factur-X / XRechnung hybrid invoices (PDF/A-3).
/// </summary>
public record PdfEmbeddedFile(string Name, byte[] Data);

/// <summary>
///     Result of a text-layer extraction pass over a PDF.
/// </summary>
public record PdfTextExtractionResult(
    IReadOnlyList<PdfPageText> Pages,
    IReadOnlyList<PdfEmbeddedFile> EmbeddedFiles);

/// <summary>
///     Extracts the embedded text layer and embedded file attachments from a PDF —
///     WITHOUT rasterizing or OCR. Born-digital PDFs carry their text losslessly in the
///     text layer; re-reading them via OCR re-introduces recognition errors (l/i, 0/O)
///     on exactly the fields that matter (amounts, IBANs, invoice numbers).
///     Implementations are swappable (PdfPig today; IronPDF would be a drop-in
///     alternative if the Iron Suite license question is ever resolved in its favor).
/// </summary>
public interface IPdfTextExtractor
{
    /// <summary>
    ///     Reads the text layer of every page plus any embedded file attachments.
    /// </summary>
    /// <param name="pdfBytes">The PDF file content.</param>
    /// <param name="textLayerMinChars">
    ///     Minimum number of non-whitespace characters for a page to count as having a
    ///     usable text layer.
    /// </param>
    PdfTextExtractionResult Extract(byte[] pdfBytes, int textLayerMinChars);
}

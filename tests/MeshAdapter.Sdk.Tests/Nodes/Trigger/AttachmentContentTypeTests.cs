using System.Text;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5338. The shared Stage Document pipeline answers "is this a PDF?" from the DECLARED MIME type
/// and sends everything else through its image-to-PDF branch, which replaces the stored bytes with a
/// render of an <c>&lt;img&gt;</c> tag. A PDF declared as <c>application/octet-stream</c> is therefore
/// not merely mislabelled, it is destroyed — on prod-1/gastroacker a 90402-byte invoice was stored as
/// a 5606-byte blank page while the mail was already flagged read. 43 of the 303 mails in that
/// mailbox's open fiscal year carry a PDF declared that way.
///
/// The Graph channel normalised this from AB#4433; these cases pin the behaviour now that the IMAP
/// channel shares it.
/// </summary>
public class AttachmentContentTypeTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));

    /// <summary>A realistic PDF prefix: the magic header followed by a version and body bytes.</summary>
    private static string PdfBytes() => B64("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n1 0 obj");

    [Fact]
    public void OctetStreamPdf_IsCorrected()
    {
        // The exact shape observed on prod-1/gastroacker.
        var result = AttachmentContentType.NormalizePdf(
            "Rechnung 2634.pdf", "application/octet-stream", PdfBytes());

        Assert.Equal("application/pdf", result);
    }

    [Fact]
    public void DeclaredPdf_IsLeftAlone()
    {
        Assert.Equal("application/pdf",
            AttachmentContentType.NormalizePdf("invoice.pdf", "application/pdf", PdfBytes()));
    }

    [Fact]
    public void DeclaredPdf_IsCanonicalisedAndNotSecondGuessedByContent()
    {
        // A sender who says "pdf" is taken at their word — re-deciding from the bytes would only add
        // a second way for the type to change underneath the pipeline. The CASING is corrected
        // though: the stager's gate is an exact comparison against "application/pdf", so an
        // "application/PDF" would be routed into the image branch exactly like an octet-stream one.
        Assert.Equal("application/pdf",
            AttachmentContentType.NormalizePdf("odd.pdf", "application/PDF", B64("not really a pdf")));
    }

    [Fact]
    public void MagicHeader_RescuesAnAttachmentWithoutAUsableName()
    {
        // MimeKit falls back to "unknown" when the part carries no filename.
        Assert.Equal("application/pdf",
            AttachmentContentType.NormalizePdf("unknown", "application/octet-stream", PdfBytes()));
    }

    [Fact]
    public void PdfExtension_RescuesAnAttachmentWhoseBytesAreNotAvailable()
    {
        // Data stays null when the part is not a MimePart with content.
        Assert.Equal("application/pdf",
            AttachmentContentType.NormalizePdf("Rechnung.PDF", "application/octet-stream", null));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("text/calendar")]
    public void NonPdfTypes_AreNotTouched(string declared)
    {
        // Receipt photos must keep their image type — IsLikelyReceiptImage keys on "image/".
        Assert.Equal(declared,
            AttachmentContentType.NormalizePdf("IMG_0177.jpeg", declared, B64("\xFF\xD8\xFFsomejpeg")));
    }

    [Fact]
    public void GenuineOctetStream_StaysOctetStream()
    {
        Assert.Equal("application/octet-stream",
            AttachmentContentType.NormalizePdf("archive.zip", "application/octet-stream", B64("PK\x03\x04junk")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("QQ")]
    public void UndecodableOrShortContent_DoesNotThrow(string content)
    {
        // A malformed payload must not take the whole poll down with it.
        Assert.Equal("application/octet-stream",
            AttachmentContentType.NormalizePdf("mystery.bin", "application/octet-stream", content));
    }

    [Fact]
    public void CorrectedType_MakesTheAttachmentStageableAsADocument()
    {
        // The end-to-end point: after normalisation the attachment both stages AND survives the
        // stager's content-type gate, which is what failed on prod-1.
        var declared = new AttachmentData
        {
            FileName = "Rechnung 2634.pdf", ContentType = "application/octet-stream",
            Length = 90402, Data = PdfBytes()
        };
        Assert.True(declared.IsLikelyDocument, "reaches the stager on the file name alone today");

        declared.ContentType = AttachmentContentType.NormalizePdf(
            declared.FileName, declared.ContentType, declared.Data);

        Assert.Equal("application/pdf", declared.ContentType);
        Assert.True(declared.IsLikelyDocument);
    }
}

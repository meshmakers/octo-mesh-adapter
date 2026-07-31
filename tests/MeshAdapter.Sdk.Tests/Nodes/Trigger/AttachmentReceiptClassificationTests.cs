using System.Text.Json;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#4647: employees sometimes send a receipt as a photo pasted inline into the mail
/// body (Graph reports it as an inline image attachment). Such a photo must be staged
/// as its own document, while signature logos / office artwork must NOT. Since a real
/// receipt photo can be smaller than a signature logo, the discriminator is the
/// camera/scanner/messenger file-name pattern (plus an image content type), not size.
/// These cases are the real attachments observed in the prod-1 accounting mailbox.
/// </summary>
public class AttachmentReceiptClassificationTests
{
    private static AttachmentData Att(string name, string contentType, long length, bool inline = false) =>
        new() { FileName = name, ContentType = contentType, Length = length, IsInline = inline, Data = "AA==" };

    [Theory]
    // Real receipts (camera / scanner / messenger names, image content type).
    [InlineData("IMG_0177.jpeg", "image/jpeg", 29672)]          // Sandra's embedded iPhone receipt (29 KB!)
    [InlineData("PXL_20250821_082621211~2.jpg", "image/jpeg", 844269)] // Pixel photo receipt
    [InlineData("IMG_1234.HEIC", "image/heic", 1_500_000)]      // iPhone HEIC
    [InlineData("Screenshot 2026-07-30.png", "image/png", 120000)]
    [InlineData("signal-2026-07-30-12-00-00.jpg", "image/jpeg", 250000)]
    public void ReceiptPhotos_AreLikelyReceiptImages(string name, string ct, long len)
    {
        var a = Att(name, ct, len, inline: true);
        Assert.True(a.IsLikelyReceiptImage, $"{name} should be a receipt image");
        Assert.True(a.IsLikelyDocument);
    }

    [Theory]
    // Signature logos / office artwork observed inline in the mailbox — must be ignored.
    [InlineData("image001.png", "image/png", 454344)]  // large, but not a camera name
    [InlineData("westbahn.jpg", "image/jpeg", 77702)]   // brand logo, jpeg
    [InlineData("HMRV_Logo_web.png", "image/png", 31988)]
    [InlineData("~WRD0267.jpg", "image/jpeg", 4575)]    // Word inline artwork
    [InlineData("external-link.png", "image/png", 649)]
    [InlineData("rci.png", "image/png", 748)]
    [InlineData("Outlook-3txghppo.png", "image/png", 820)]
    [InlineData("image005.gif", "image/gif", 986725)]  // animated/decorative gif, excluded even when large
    public void SignatureLogos_AreNotReceiptImages(string name, string ct, long len)
    {
        var a = Att(name, ct, len, inline: true);
        Assert.False(a.IsLikelyReceiptImage, $"{name} must not be treated as a receipt");
        Assert.False(a.IsLikelyDocument);
    }

    [Fact]
    public void TinyCameraNamedImage_BelowFloor_IsIgnored()
    {
        // A camera-named but trivially small image (tracker / thumbnail) is not a receipt.
        Assert.False(Att("IMG_0001.jpg", "image/jpeg", 3000, inline: true).IsLikelyReceiptImage);
    }

    [Theory]
    // Regular (non-inline) image attachments are a deliberate act by the sender, so any
    // name counts — including names that would be rejected inline (IMAP/Signal/Teams and
    // ordinary email attachments arrive this way; those channels never expose inline parts).
    [InlineData("rechnung.jpg", "image/jpeg")]
    [InlineData("receipt.png", "image/png")]
    [InlineData("image001.png", "image/png")] // same name as a logo, but deliberately attached
    public void DeliberatelyAttachedImages_AreStaged_RegardlessOfName(string name, string ct)
    {
        var a = Att(name, ct, 200000, inline: false);
        Assert.True(a.IsLikelyReceiptImage);
        Assert.True(a.IsLikelyDocument);
    }

    [Fact]
    public void InlineImage_WithoutCameraName_IsSignatureLogo_EvenIfLarge()
    {
        // The inline + non-camera-name combination is the signature-logo signal.
        Assert.False(Att("image001.png", "image/png", 454344, inline: true).IsLikelyReceiptImage);
    }

    [Theory]
    [InlineData("Rechnung_123.pdf", "application/pdf")]
    [InlineData("Beleg2.pdf", "application/octet-stream")] // AB#4433: mislabeled PDF still counts by extension
    public void Pdfs_AreLikelyDocumentsButNotReceiptImages(string name, string ct)
    {
        var a = Att(name, ct, 200000);
        Assert.True(a.IsLikelyDocument);
        Assert.False(a.IsLikelyReceiptImage);
    }

    [Fact]
    public void Email_WithReceiptImage_IsStageable_AndBodyRenderSuppressed()
    {
        var mail = new EmailData
        {
            Subject = "Bauhaus die zweite",
            Attachments = { Att("IMG_0177.jpeg", "image/jpeg", 29672, inline: true) }
        };
        Assert.True(mail.HasReceiptImageAttachment);
        Assert.True(mail.HasStageableAttachment);   // → body-render branch is skipped
        Assert.False(mail.HasPdfAttachment);
    }

    [Fact]
    public void Email_PdfPlusSignatureLogo_StagesPdf_NotLogo()
    {
        var mail = new EmailData
        {
            Subject = "Invoice",
            Attachments =
            {
                Att("Rechnung.pdf", "application/pdf", 120000),
                Att("image001.png", "image/png", 454344, inline: true) // signature logo
            }
        };
        Assert.True(mail.HasStageableAttachment);
        Assert.False(mail.HasReceiptImageAttachment); // the logo is not a receipt
        Assert.True(mail.Attachments[0].IsLikelyDocument);
        Assert.False(mail.Attachments[1].IsLikelyDocument);
    }

    [Fact]
    public void Email_LogoOnly_IsNotStageable_SoBodyRenders()
    {
        var mail = new EmailData
        {
            Subject = "Question about the project",
            Attachments = { Att("image001.png", "image/png", 454344, inline: true) }
        };
        Assert.False(mail.HasStageableAttachment); // → body is rendered as the receipt
    }

    [Fact]
    public void ComputedFlags_SerializeUnderTheNamesThePipelineReads()
    {
        // The pipeline gates read $.key.HasStageableAttachment / $.key.Attachments[].IsLikelyDocument
        // etc. These are computed getters; the trigger emits the whole EmailBatch and the pipeline
        // framework serializes it with System.Text.Json (PascalCase, no camelCase policy — the
        // existing pipeline already reads $.key.ContentType and $.key.HasPdfAttachment this way).
        // This locks in that the new getters serialize under exactly those JSONPath names.
        var batch = new EmailBatch
        {
            Emails = { new EmailData
            {
                Subject = "Bauhaus die zweite",
                Attachments = { Att("IMG_0177.jpeg", "image/jpeg", 29672, inline: true) }
            } }
        };

        var json = JsonSerializer.Serialize(batch);

        Assert.Contains("\"HasStageableAttachment\":true", json);
        Assert.Contains("\"HasReceiptImageAttachment\":true", json);
        Assert.Contains("\"HasPdfAttachment\":false", json);
        Assert.Contains("\"IsLikelyDocument\":true", json);
        Assert.Contains("\"IsLikelyReceiptImage\":true", json);
        Assert.Contains("\"IsInline\":true", json);
    }
}

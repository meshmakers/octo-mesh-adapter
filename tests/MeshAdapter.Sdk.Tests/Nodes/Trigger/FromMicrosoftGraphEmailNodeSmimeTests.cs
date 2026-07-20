using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using MimeKit;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#4433: S/MIME-signed invoices (e.g. Magenta) arrive as a single <c>smime.p7m</c>
/// container whose real PDF is inside the SIGNED envelope — either an opaque PKCS#7
/// signed-data blob or a clear-signed <c>multipart/signed</c> MIME entity. These tests
/// build genuine samples of BOTH formats and assert that
/// <see cref="FromMicrosoftGraphEmailNode.TryExtractSmimePdfAttachments"/> unwraps them to
/// the inner PDFs — including a PDF mislabeled <c>application/octet-stream</c> — and that
/// malformed input falls back safely to "no PDFs".
/// </summary>
public class FromMicrosoftGraphEmailNodeSmimeTests
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfake invoice body\n%%EOF");

    [Fact]
    public void UnwrapsSignedContainer_SurfacesInnerPdfs_IncludingMislabeledOctetStream()
    {
        // inner MIME: a text body, a proper application/pdf, and an octet-stream .pdf
        var multipart = new Multipart("mixed");
        multipart.Add(new TextPart("plain") { Text = "See the attached invoice." });
        multipart.Add(PdfPart("application", "pdf", "Rechnung_123.pdf"));
        multipart.Add(PdfPart("application", "octet-stream", "Beleg2.pdf"));

        var container = SignEncapsulated(WriteMime(multipart));

        var ok = FromMicrosoftGraphEmailNode.TryExtractSmimePdfAttachments(container, out var pdfs);

        Assert.True(ok);
        Assert.Equal(2, pdfs.Count);
        Assert.All(pdfs, p => Assert.Equal("application/pdf", p.ContentType));
        Assert.Contains(pdfs, p => p.FileName == "Rechnung_123.pdf");
        Assert.Contains(pdfs, p => p.FileName == "Beleg2.pdf");
        // the surfaced Data is the raw PDF (base64) — decodes back to the %PDF bytes
        Assert.NotNull(pdfs[0].Data);
        var decoded = Convert.FromBase64String(pdfs[0].Data!);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(decoded, 0, 5));
    }

    [Fact]
    public void UnwrapsClearSignedMultipart_SurfacesInnerPdf()
    {
        // Clear-signed S/MIME: the smime.p7m bytes are a multipart/signed MIME entity —
        // first child = original content, second child = detached pkcs7-signature.
        // The extraction path does not verify signatures, so a dummy signature part
        // is sufficient.
        var inner = new Multipart("mixed");
        inner.Add(new TextPart("plain") { Text = "Ihre Rechnung im Anhang." });
        inner.Add(PdfPart("application", "pdf", "Magenta_Rechnung.pdf"));

        var signed = new Multipart("signed");
        signed.ContentType.Parameters.Add("protocol", "application/pkcs7-signature");
        signed.ContentType.Parameters.Add("micalg", "sha-256");
        signed.Add(inner);
        signed.Add(new MimePart("application", "pkcs7-signature")
        {
            Content = new MimeContent(new MemoryStream(new byte[] { 0x30, 0x82, 0x01, 0x00 })),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "smime.p7s"
        });

        var container = WriteMime(signed);

        var ok = FromMicrosoftGraphEmailNode.TryExtractSmimePdfAttachments(container, out var pdfs);

        Assert.True(ok);
        var pdf = Assert.Single(pdfs);
        Assert.Equal("Magenta_Rechnung.pdf", pdf.FileName);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.NotNull(pdf.Data);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(Convert.FromBase64String(pdf.Data!), 0, 5));
    }

    [Fact]
    public void SignedContainerWithoutPdf_ReturnsFalse()
    {
        var multipart = new Multipart("mixed");
        multipart.Add(new TextPart("plain") { Text = "Body only, no attachment." });

        var container = SignEncapsulated(WriteMime(multipart));

        Assert.False(FromMicrosoftGraphEmailNode.TryExtractSmimePdfAttachments(container, out var pdfs));
        Assert.Empty(pdfs);
    }

    [Theory]
    [InlineData("not valid base64 %%%")]
    [InlineData("YWJjZGVmZ2g=")] // valid base64, but not a CMS structure
    public void MalformedContainer_FallsBackToNoPdfs(string container)
    {
        Assert.False(FromMicrosoftGraphEmailNode.TryExtractSmimePdfAttachments(container, out var pdfs));
        Assert.Empty(pdfs);
    }

    private static MimePart PdfPart(string mediaType, string mediaSubtype, string fileName) =>
        new(mediaType, mediaSubtype)
        {
            Content = new MimeContent(new MemoryStream(PdfBytes)),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName
        };

    private static string WriteMime(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        entity.WriteTo(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Signs <paramref name="innerMimeBase64"/> as an encapsulated (opaque) PKCS#7
    /// SignedData container with an ephemeral self-signed cert and returns it base64-encoded,
    /// mirroring what Graph delivers as the smime.p7m attachment contentBytes.</summary>
    private static string SignEncapsulated(string innerMimeBase64)
    {
        var innerBytes = Convert.FromBase64String(innerMimeBase64);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AB4433 S/MIME Test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // PFX round-trip so the private key is fully associated for CMS signing on all platforms
        using var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);

        var signedCms = new SignedCms(new ContentInfo(innerBytes)); // encapsulated (detached=false)
        signedCms.ComputeSignature(new CmsSigner(cert) { IncludeOption = X509IncludeOption.EndCertOnly });
        return Convert.ToBase64String(signedCms.Encode());
    }
}

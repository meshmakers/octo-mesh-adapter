namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     Corrects the MIME type a sender declared for an attachment, so every inbound channel hands
///     downstream the same answer to "is this a PDF?".
///     <para>
///         This matters because the shared Stage Document pipeline decides that question on the
///         DECLARED type alone: anything that is not <c>application/pdf</c> goes through its
///         image-to-PDF branch, which wraps the payload in an <c>&lt;img src="data:…"&gt;</c> and
///         REPLACES the stored bytes with the render. A PDF declared as
///         <c>application/octet-stream</c> — which many senders do — is therefore not merely
///         mislabelled, it is destroyed: on prod-1/gastroacker a 90402-byte invoice was stored as a
///         5606-byte blank page, and the mail had already been flagged read. 43 of the 303 mails in
///         that mailbox's current fiscal year carry a PDF declared this way.
///     </para>
///     <para>
///         The Microsoft Graph channel has normalised this since AB#4433; AB#5338 shares the logic so
///         the IMAP channel stops being the odd one out.
///     </para>
/// </summary>
internal static class AttachmentContentType
{
    private const string Pdf = "application/pdf";

    /// <summary>
    ///     Returns <c>application/pdf</c> when the attachment is a PDF that was declared as something
    ///     else, and the declared type unchanged otherwise. Keys on the <c>.pdf</c> file-name extension
    ///     first — matching the MIME map in <see cref="FromMicrosoftGraphNode" /> — and falls back to
    ///     sniffing the <c>%PDF-</c> magic header, which also catches an attachment whose name carries
    ///     no usable extension.
    /// </summary>
    internal static string NormalizePdf(string fileName, string contentType, string? base64Content)
    {
        // Canonical casing, not the declared spelling: the Stage Document gate is an exact string
        // comparison against "application/pdf", so a sender's "application/PDF" would fall into the
        // image branch just like an octet-stream one.
        if (string.Equals(contentType, Pdf, StringComparison.OrdinalIgnoreCase))
        {
            return Pdf;
        }

        return fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || StartsWithPdfHeader(base64Content)
            ? Pdf
            : contentType;
    }

    /// <summary>
    ///     True when the base64 payload starts with the <c>%PDF-</c> signature.
    /// </summary>
    private static bool StartsWithPdfHeader(string? base64Content)
    {
        if (string.IsNullOrEmpty(base64Content))
        {
            return false;
        }

        // 8 base64 chars decode to 6 bytes — enough for the 5-byte "%PDF-" signature.
        var prefix = base64Content.Length >= 8 ? base64Content[..8] : base64Content;
        try
        {
            var bytes = Convert.FromBase64String(prefix);
            return bytes.Length >= 5 &&
                   bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 &&
                   bytes[3] == 0x46 && bytes[4] == 0x2D; // %PDF-
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

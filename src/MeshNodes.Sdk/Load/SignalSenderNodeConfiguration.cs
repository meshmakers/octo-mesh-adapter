using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for the SignalSender pipeline node — sends a message (and optional
/// attachment) through a signal-cli-rest-api bridge via <c>POST {ApiUrl}/v2/send</c>.
/// </summary>
/// <remarks>
/// The bridge base URL and the sending account number are plain node configuration
/// (not secrets — the local bridge is unauthenticated), following the
/// <c>{Field}</c> + <c>{Field}Path</c> convention: when the *Path variant is
/// non-empty the value is read from the data context; otherwise the literal is used.
/// Prototype context: AB#4406 (Epic AB#3295).
/// </remarks>
[NodeName("SignalSender", 1)]
public record SignalSenderNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Base URL of the signal-cli-rest-api bridge, e.g. <c>http://localhost:8080</c>.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ApiUrl { get; set; }

    /// <summary>
    /// The bridge's registered account number that sends the message, e.g. <c>+4366012345678</c>.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string Number { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds. Default 30.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Literal recipient number (destination), e.g. <c>+4366098765432</c>.
    /// </summary>
    [PropertyGroup("Message", 0)]
    public string? Recipient { get; set; }

    /// <summary>
    /// JSONPath to resolve the recipient number from the data context
    /// (e.g. the inbound sender <c>$.key.Source</c>).
    /// </summary>
    [PropertyGroup("Message", 1, "jsonpath")]
    public string? RecipientPath { get; set; }

    /// <summary>
    /// Literal message text.
    /// </summary>
    [PropertyGroup("Message", 2)]
    public string? Message { get; set; }

    /// <summary>
    /// JSONPath to resolve the message text from the data context.
    /// </summary>
    [PropertyGroup("Message", 3, "jsonpath")]
    public string? MessagePath { get; set; }

    /// <summary>
    /// Optional JSONPath to a base64-encoded attachment (e.g. a rendered PDF) sent
    /// with the message via the bridge's <c>base64_attachments</c> field.
    /// </summary>
    [PropertyGroup("Attachment", 0, "jsonpath")]
    public string? AttachmentBase64Path { get; set; }

    /// <summary>
    /// Literal MIME content type of the attachment (e.g. <c>application/pdf</c>).
    /// Used to build the attachment data URI; ignored when no attachment is set.
    /// </summary>
    [PropertyGroup("Attachment", 1)]
    public string? AttachmentContentType { get; set; }

    /// <summary>
    /// JSONPath to resolve the attachment content type from the data context.
    /// </summary>
    [PropertyGroup("Attachment", 2, "jsonpath")]
    public string? AttachmentContentTypePath { get; set; }

    /// <summary>
    /// Literal attachment filename (e.g. <c>invoice.pdf</c>).
    /// </summary>
    [PropertyGroup("Attachment", 3)]
    public string? AttachmentFilename { get; set; }

    /// <summary>
    /// JSONPath to resolve the attachment filename from the data context.
    /// </summary>
    [PropertyGroup("Attachment", 4, "jsonpath")]
    public string? AttachmentFilenamePath { get; set; }
}

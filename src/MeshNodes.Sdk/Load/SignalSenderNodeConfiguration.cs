using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for the SignalSender pipeline node — sends a message (and optional
/// attachment) through a signal-cli-rest-api bridge via <c>POST {ApiUrl}/v2/send</c>.
/// </summary>
/// <remarks>
/// Number/ApiUrl resolution (AB#5145): the tenant's registered
/// <c>System.Communication/SignalChannel</c> singleton (activated via the Studio self-service,
/// AB#5143) wins and needs NO configuration here at all; the connection/settings properties
/// below are the deprecated legacy fallback (not secrets — the cluster-local bridge is
/// unauthenticated). Message properties follow the <c>{Field}</c> + <c>{Field}Path</c>
/// convention: when the *Path variant is non-empty the value is read from the data context;
/// otherwise the literal is used. Prototype context: AB#4406 (Epic AB#3295).
/// </remarks>
[NodeName("SignalSender", 1)]
public record SignalSenderNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// DEPRECATED legacy fallback (AB#5145): base URL of the signal-cli-rest-api bridge,
    /// e.g. <c>http://localhost:8080</c>. Only read when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c>; a <see cref="SettingsConfiguration"/> value
    /// still takes precedence over this literal. May be omitted entirely on new-style pipelines.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// DEPRECATED legacy fallback (AB#5145): the bridge's registered account number that sends
    /// the message, e.g. <c>+4366012345678</c>. Only read when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c>; a <see cref="SettingsConfiguration"/> value
    /// still takes precedence over this literal. May be omitted entirely on new-style pipelines.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string Number { get; set; } = null!;

    /// <summary>
    /// HTTP request timeout in seconds. Default 30.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// DEPRECATED legacy fallback (AB#5145): optional well-known name of a configuration entity
    /// that carries the bridge number / URL (e.g. the accounting app's
    /// <c>SignalImportSettings</c>), reachable from the pipeline via a
    /// <c>System.Communication/Uses</c> association; the attribute names it reads are
    /// <see cref="NumberAttribute"/> / <see cref="ApiUrlAttribute"/>, and values found here
    /// override the node properties. Only consulted when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c> — new-style pipelines omit this entirely.
    /// </summary>
    [PropertyGroup("Settings", 0)]
    public string? SettingsConfiguration { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the bridge number.</summary>
    [PropertyGroup("Settings", 1)]
    public string? NumberAttribute { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the bridge base URL.</summary>
    [PropertyGroup("Settings", 2)]
    public string? ApiUrlAttribute { get; set; }

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

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for sending an email
/// </summary>
[NodeName("SendEMail", 1)]
public record EMailSenderNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Name of the global configuration for the email server
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Name of the global configuration for the CSS style
    /// </summary>
    [PropertyGroup("Output", 0)]
    public string? CssConfiguration { get; set; }

    /// <summary>
    /// Source path for the email subject
    /// </summary>
    [PropertyGroup("Email", 0, "jsonpath")]
    public required string SubjectPath { get; set; }

    /// <summary>
    /// Source path to Recipient email addresses
    /// </summary>
    [PropertyGroup("Email", 1, "jsonpath")]
    public required string ToPath { get; set; }


    /// <summary>
    /// Optional path to the cc email addresses
    /// </summary>
    [PropertyGroup("Email", 2, "jsonpath")]
    public string? CcPath { get; set; }

    /// <summary>
    /// Optional list of cc email addresses
    /// </summary>
    [PropertyGroup("Email", 3)]
    public ICollection<string>? CcAddresses { get; set; }


    /// <summary>
    /// Optional path to the bcc email addresses
    /// </summary>
    [PropertyGroup("Email", 4, "jsonpath")]
    public string? BccPath { get; set; }


    /// <summary>
    /// Optional list of bcc email addresses
    /// </summary>
    [PropertyGroup("Email", 5)]
    public ICollection<string>? BccAddresses { get; set; }



    /// <summary>
    /// The path the the attatchmet RtId
    /// </summary>
    [PropertyGroup("Email", 6, "jsonpath")]
    public string? AttachmentRtIdPath { get; set; }

    /// <summary>
    /// The RtId of the attachment
    /// </summary>
    [PropertyGroup("Email", 7)]
    public string? AttachmentRtId { get; set; }

    /// <summary>
    /// The file name of the attachment
    /// </summary>
    [PropertyGroup("Email", 8)]
    public string? AttachmentFileName { get; set; }

    /// <summary>
    /// Content type of the attachment.
    /// </summary>
    [PropertyGroup("Email", 9)]
    public string? AttachmentContentType { get; set; } = "application/octet-stream";
}
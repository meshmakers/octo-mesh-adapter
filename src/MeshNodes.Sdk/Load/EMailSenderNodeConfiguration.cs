using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Name of the global configuration for the CSS style
    /// </summary>
    public string? CssConfiguration { get; set; }

    /// <summary>
    /// Source path for the email subject
    /// </summary>
    public required string SubjectPath { get; set; }
    
    /// <summary>
    /// Source path to Recipient email addresses
    /// </summary>
    public required string ToPath { get; set; }
    
    
    /// <summary>
    /// Optional path to the cc email addresses
    /// </summary>
    public string? CcPath { get; set; }
    
    /// <summary>
    /// Optional list of cc email addresses
    /// </summary>
    public ICollection<string>? CcAddresses { get; set; }
    
    
    /// <summary>
    /// Optional path to the bcc email addresses
    /// </summary>
    public string? BccPath { get; set; }
    
    
    /// <summary>
    /// Optional list of bcc email addresses
    /// </summary>
    public ICollection<string>? BccAddresses { get; set; }
    
    
    
    /// <summary>
    /// The path the the attatchmet RtId
    /// </summary>
    public string? AttachmentRtIdPath { get; set; }
    
    /// <summary>
    /// The RtId of the attachment
    /// </summary>
    public string? AttachmentRtId { get; set; }
    
    /// <summary>
    /// The file name of the attachment
    /// </summary>
    public string? AttachmentFileName { get; set; }
    
    /// <summary>
    /// Content type of the attachment.
    /// </summary>
    public string? AttachmentContentType { get; set; } = "application/octet-stream";
}
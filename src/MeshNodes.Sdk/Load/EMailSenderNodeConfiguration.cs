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
}
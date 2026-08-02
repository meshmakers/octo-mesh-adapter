using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for sending an email via Microsoft Graph
/// (<c>POST /users/{mailbox}/sendMail</c>), reusing the app-only credentials of a
/// <c>MicrosoftGraphConfiguration</c> (AzureTenantId/ClientId/ClientSecret). The
/// outbound counterpart of <c>FromMicrosoftGraphEmail@1</c>; requires Graph
/// application permission <c>Mail.Send</c>. The body is read from <c>Path</c>
/// (Markdown, rendered to HTML).
/// </summary>
[NodeName("SendMicrosoftGraphEmail", 1)]
public record SendMicrosoftGraphEmailNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Name of the global MicrosoftGraphConfiguration holding
    /// AzureTenantId/ClientId/ClientSecret.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Mailbox to send from (e.g. <c>accounting@meshmakers.io</c>). Literal value.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? Mailbox { get; set; }

    /// <summary>
    /// Optional path to the sender mailbox (overrides <see cref="Mailbox"/>).
    /// </summary>
    [PropertyGroup("Connection", 2, "jsonpath")]
    public string? MailboxPath { get; set; }

    /// <summary>
    /// Source path for the email subject.
    /// </summary>
    [PropertyGroup("Email", 0, "jsonpath")]
    public required string SubjectPath { get; set; }

    /// <summary>
    /// Source path to the recipient email address(es) — a single string or an array.
    /// </summary>
    [PropertyGroup("Email", 1, "jsonpath")]
    public required string ToPath { get; set; }

    /// <summary>
    /// When true, log the error and continue instead of failing the pipeline on a
    /// send error.
    /// </summary>
    [PropertyGroup("Behavior", 0)]
    public bool ContinueOnError { get; set; }

    /// <summary>
    /// HTTP timeout in seconds (token acquisition + sendMail).
    /// </summary>
    [PropertyGroup("Behavior", 1)]
    public int TimeoutSeconds { get; set; } = 30;
}

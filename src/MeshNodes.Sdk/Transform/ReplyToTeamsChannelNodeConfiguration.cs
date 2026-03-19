using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for sending a message to a Microsoft Teams channel via Incoming Webhook
/// </summary>
[NodeName("ReplyToTeamsChannel", 1)]
public record ReplyToTeamsChannelNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// The Teams Incoming Webhook URL (static value)
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// JSON path to the webhook URL in the data context (alternative to WebhookUrl)
    /// </summary>
    public string? WebhookUrlPath { get; set; }

    /// <summary>
    /// JSON path to the reply message body (HTML or plain text)
    /// </summary>
    public string? MessageBodyPath { get; set; }

    /// <summary>
    /// Static reply message body with ${jsonPath} placeholder support
    /// </summary>
    public string? MessageBody { get; set; }

    /// <summary>
    /// Optional title displayed as a header in the Teams message card
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Theme color for the message card (hex without #, e.g. "0076D7")
    /// </summary>
    public string ThemeColor { get; set; } = "0076D7";

    /// <summary>
    /// Whether to continue pipeline execution if sending the message fails
    /// </summary>
    public bool ContinueOnError { get; set; } = true;
}

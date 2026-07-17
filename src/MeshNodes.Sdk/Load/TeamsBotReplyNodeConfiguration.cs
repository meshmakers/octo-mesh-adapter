using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for the TeamsBotReply pipeline node — sends a reply message into an
/// ongoing Microsoft Teams conversation via the Bot Framework REST API
/// (<c>POST {serviceUrl}/v3/conversations/{conversationId}/activities</c>).
/// Outbound counterpart of <c>FromTeamsBot@1</c>.
/// </summary>
/// <remarks>
/// The bot credentials (App ID + secret) are read from a
/// <c>MicrosoftGraphConfiguration</c> global configuration entity resolved by
/// <see cref="ServerConfiguration"/> (its <c>ClientId</c>/<c>ClientSecret</c> double as the
/// bot's App ID/secret — the concept assumes one shared Azure AD App Registration).
/// The conversation routing values (<c>serviceUrl</c>, <c>conversationId</c>) originate
/// from the inbound activity captured by <c>FromTeamsBot@1</c> and are read from the data
/// context via the <c>*Path</c> properties.
/// </remarks>
[NodeName("TeamsBotReply", 1)]
public record TeamsBotReplyNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// WellKnownName of the <c>MicrosoftGraphConfiguration</c> that carries the bot
    /// App ID (<c>ClientId</c>) and secret (<c>ClientSecret</c>).
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// JSONPath to the Bot Framework <c>serviceUrl</c> of the originating conversation
    /// (captured by <c>FromTeamsBot@1</c>). Default <c>$.Conversation.ServiceUrl</c>.
    /// </summary>
    [PropertyGroup("Conversation", 0, "jsonpath")]
    public string ServiceUrlPath { get; set; } = "$.Conversation.ServiceUrl";

    /// <summary>
    /// JSONPath to the conversation id to reply into. Default <c>$.Conversation.ConversationId</c>.
    /// </summary>
    [PropertyGroup("Conversation", 1, "jsonpath")]
    public string ConversationIdPath { get; set; } = "$.Conversation.ConversationId";

    /// <summary>
    /// Optional JSONPath to the activity id to reply to (threaded reply). When omitted the
    /// message is posted as a new activity in the conversation. Default
    /// <c>$.Conversation.ActivityId</c>.
    /// </summary>
    [PropertyGroup("Conversation", 2, "jsonpath")]
    public string? ReplyToActivityIdPath { get; set; } = "$.Conversation.ActivityId";

    /// <summary>
    /// JSONPath to the reply text (e.g. the AI answer or an upload acknowledgement).
    /// </summary>
    [PropertyGroup("Message", 0, "jsonpath")]
    public string? MessageBodyPath { get; set; }

    /// <summary>
    /// Literal reply text (used when <see cref="MessageBodyPath"/> is empty or resolves to blank).
    /// </summary>
    [PropertyGroup("Message", 1, "textarea")]
    public string? MessageBody { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds. Default 30.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to continue pipeline execution if sending the reply fails. Default true.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool ContinueOnError { get; set; } = true;
}

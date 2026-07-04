using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for Anthropic AI Query node that uses Claude to extract information from data
/// </summary>
[NodeName("AnthropicAiQuery", 1)]
public record AnthropicAiQueryNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the AiConfiguration entity in GlobalConfiguration to load the API key from.
    /// When set, the API key is read from the configuration entity and never exposed in the data context.
    /// Takes precedence over <see cref="ApiKey"/>.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string? ApiKeyConfigurationName { get; set; }

    /// <summary>
    /// The Anthropic API key for authentication.
    /// Prefer using <see cref="ApiKeyConfigurationName"/> to avoid exposing the key in pipeline definitions.
    /// </summary>
    [PropertyGroup("Connection", 1, "password")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// The Claude model to use (e.g., "claude-sonnet-4-6", "claude-opus-4-1").
    /// Optional on the node when the referenced AiConfiguration supplies <c>aiModel</c>; that
    /// value takes precedence. There is deliberately no hard-coded default model — a pinned model
    /// id goes out of date, so the model must be provided explicitly (node or AiConfiguration).
    /// </summary>
    [PropertyGroup("AI Configuration", 0)]
    public string? Model { get; set; }

    /// <summary>
    /// The question/prompt to ask the AI about the data
    /// </summary>
    [PropertyGroup("AI Configuration", 1, "textarea")]
    public required string Question { get; set; }

    /// <summary>
    /// Optional context data paths to include in the query (e.g., ["$.ExtractedText", "$.DocumentMetadata"])
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string[]? DataPaths { get; set; }

    /// <summary>
    /// System prompt to set the context for the AI
    /// </summary>
    [PropertyGroup("AI Configuration", 2, "textarea")]
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";

    /// <summary>
    /// Maximum tokens for the response
    /// </summary>
    [PropertyGroup("AI Configuration", 3)]
    public int MaxTokens { get; set; } = 1000;

    /// <summary>
    /// Temperature for response generation (0.0 to 1.0)
    /// </summary>
    [PropertyGroup("AI Configuration", 4)]
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Expected response format (e.g., "json", "text")
    /// </summary>
    [PropertyGroup("Output", 0)]
    public string ResponseFormat { get; set; } = "json";

    /// <summary>
    /// Whether to include the raw AI response in the output
    /// </summary>
    [PropertyGroup("Output", 1)]
    public bool IncludeRawResponse { get; set; } = false;

    /// <summary>
    /// Output path for the raw AI response (if IncludeRawResponse is true)
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? RawResponseOutputPath { get; set; }

    /// <summary>
    /// Whether to continue processing if the AI query fails
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool ContinueOnError { get; set; } = false;

    /// <summary>
    /// Optional URL of the OctoMesh MCP server (e.g. "https://localhost:5017").
    /// When set, Claude can use MCP tools to query live OctoMesh data instead of relying on static context.
    /// The tenant ID is appended automatically: {McpServerUrl}/{tenantId}/mcp
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? McpServerUrl { get; set; }

    /// <summary>
    /// Maximum number of tool use rounds to prevent infinite loops (default: 10)
    /// </summary>
    [PropertyGroup("AI Configuration", 5)]
    public int MaxToolRounds { get; set; } = 10;

    /// <summary>
    /// Optional list of MCP tool names to expose to Claude. If set, only these tools are available.
    /// Reduces context size by excluding unused tools (e.g., ["query_entities_simple", "navigate_associations"]).
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string[]? McpToolNames { get; set; }

    /// <summary>
    /// Optional well-known name of a <c>System.Communication/ServiceAccountConfiguration</c> entity
    /// used to authenticate the MCP tool calls. When set, the node acquires an OAuth2
    /// client-credentials token and sends it as a <c>Authorization: Bearer</c> header on the
    /// <c>{McpServerUrl}/{tenantId}/mcp</c> requests. Required once the MCP server enforces
    /// authentication (see AB#4315).
    /// </summary>
    [PropertyGroup("Connection", 3)]
    public string? McpServiceAccountConfigName { get; set; }

    /// <summary>
    /// Optional JSON path to conversation history array.
    /// Each entry should have "role" (user/assistant) and "content" fields.
    /// When set, previous messages are included for multi-turn conversations.
    /// </summary>
    [PropertyGroup("Paths", 3, "jsonpath")]
    public string? ConversationHistoryPath { get; set; }

    /// <summary>
    /// Sample JSON format for structured responses
    /// </summary>
    [PropertyGroup("Output", 3, "code")]
    public string JsonFormatSample { get; set; } = """
    {
      "transactionDate": "2024-01-15",
      "companyAddress": "Main St, City, Country",
      "grossTotal": 1200.00,
      "netTotal": 1000.00,
      "taxAmount": 200.00
    }
    """;
}
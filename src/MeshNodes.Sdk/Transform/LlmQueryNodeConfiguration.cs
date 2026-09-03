using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Queries an LLM with pipeline data. Provider-agnostic: supports OpenAI-compatible
/// endpoints and the native Anthropic API.
/// </summary>
[NodeName("LlmQuery", 1)]
public record LlmQueryNodeConfiguration : SourceTargetPathNodeConfiguration
{
    // ---- Connection group ----

    /// <summary>
    /// LLM provider. OpenAiCompatible covers OpenAI cloud, Azure OpenAI and self-hosted
    /// endpoints (Ollama, vLLM, Cerebras, ...); Anthropic uses the native Claude API.
    /// </summary>
    [PropertyGroup("Connection")]
    public LlmProvider Provider { get; set; } = LlmProvider.OpenAiCompatible;

    /// <summary>
    /// Endpoint URL for OpenAI-compatible backends, with trailing slash
    /// (e.g. http://localhost:11434/v1/). Null = OpenAI cloud. Ignored for Anthropic.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Well-known name of the AiConfiguration entity to load the API key from.
    /// Required for authenticated providers; leave null for backends without
    /// authentication (e.g. local Ollama). If the entity also defines an aiModel,
    /// it overrides <see cref="Model"/>.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string? ApiKeyConfigurationName { get; set; }

    // ---- AI Configuration group ----

    /// <summary>
    /// Model identifier as expected by the provider
    /// (e.g. claude-sonnet-4-6, gpt-4.1-mini, qwen2.5:7b-instruct).
    /// An aiModel set on the referenced AiConfiguration entity takes precedence.
    /// No default: pinned model ids go out of date, so a model must be provided
    /// here or on the AiConfiguration.
    /// </summary>
    [PropertyGroup("AI Configuration")]
    public string? Model { get; set; }

    /// <summary>
    /// The task or question for the model.
    /// </summary>
    [PropertyGroup("AI Configuration", 1, "textarea")]
    public required string Question { get; set; }

    /// <summary>
    /// System prompt setting role and rules for the model.
    /// </summary>
    [PropertyGroup("AI Configuration", 2, "textarea")]
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";

    /// <summary>
    /// Maximum tokens in the response.
    /// </summary>
    [PropertyGroup("AI Configuration", 3)]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature (0.0-1.0); lower is more deterministic. Null = provider
    /// default. Mutually exclusive with TopP.
    /// </summary>
    [PropertyGroup("AI Configuration / Sampling", 4)]
    public double? Temperature { get; set; } = 0.3;

    /// <summary>
    /// Nucleus-sampling threshold (0.0-1.0). Null = provider default.
    /// Mutually exclusive with Temperature.
    /// </summary>
    [PropertyGroup("AI Configuration / Sampling", 5)]
    public float? TopP { get; set; }

    /// <summary>
    /// Top-K sampling cutoff. Null = provider default. May be combined with
    /// Temperature or TopP; not honored by all providers.
    /// </summary>
    [PropertyGroup("AI Configuration / Sampling", 6)]
    public int? TopK { get; set; }

    /// <summary>
    /// Maximum call duration in seconds before cancellation (default 90).
    /// The OpenAI-compatible path is capped at 100 seconds by the OpenAI SDK.
    /// </summary>
    [PropertyGroup("AI Configuration", 7)]
    public int TimeoutSeconds { get; set; } = 90;

    // ---- Paths group ----

    /// <summary>
    /// Additional data paths whose values are appended to the prompt as context.
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string[]? DataPaths { get; set; }

    /// <summary>
    /// Path to a conversation history array of {role, content} entries,
    /// included as previous messages for multi-turn calls.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string? ConversationHistoryPath { get; set; }

    // ---- Output group ----

    /// <summary>
    /// Response format: "json" or "text".
    /// </summary>
    [PropertyGroup("Output")]
    public string ResponseFormat { get; set; } = "json";

    /// <summary>
    /// Whether to additionally write the raw model response to RawResponseOutputPath.
    /// </summary>
    [PropertyGroup("Output", 1)]
    public bool IncludeRawResponse { get; set; } = false;

    /// <summary>
    /// Output path for the raw model response (used when IncludeRawResponse is true).
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? RawResponseOutputPath { get; set; }

    /// <summary>
    /// Optional example JSON appended to the prompt to guide response shape and field
    /// semantics. Empty = no example.
    /// </summary>
    [PropertyGroup("Output", 3, "code")]
    public string JsonFormatSample { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON Schema enforced server-side via the provider's structured outputs
    /// (constrained decoding): responses are guaranteed to parse. Requires a model with
    /// structured-output support. Not applied when MCP tools are configured.
    /// </summary>
    [PropertyGroup("Output", 4, "code")]
    public string? JsonSchema { get; set; }

    /// <summary>
    /// Bounded repair for unparseable JSON responses: up to this many follow-up calls
    /// (default 1, capped at 2, 0 = off), each resending only the broken output plus
    /// the parser error - never the original context.
    /// </summary>
    [PropertyGroup("Output", 5)]
    public int MaxJsonRepairAttempts { get; set; } = 1;

    // ---- Options group ----

    /// <summary>
    /// Whether to continue the pipeline if the LLM call fails.
    /// </summary>
    [PropertyGroup("Options")]
    public bool ContinueOnError { get; set; } = false;

    // ---- MCP group ----

    /// <summary>
    /// Well-known names of McpConfiguration entities whose MCP servers provide tools
    /// for this call. Empty = plain chat mode without tools.
    /// </summary>
    [PropertyGroup("MCP", 0)]
    public IList<string> McpConfigurationNames { get; set; } = new List<string>();

    /// <summary>
    /// Optional tool allowlist (case-insensitive names). Null/empty = all tools from
    /// the configured MCP servers are offered. Restricting tools cuts prompt tokens
    /// and keeps the model on task.
    /// </summary>
    [PropertyGroup("MCP", 1)]
    public string[]? McpToolNames { get; set; }

    /// <summary>
    /// Maximum tool-call rounds before the node forces a stop (default 8).
    /// No effect when no MCP tools are configured.
    /// </summary>
    [PropertyGroup("MCP", 2)]
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>
    /// Maximum characters of a single MCP tool result passed back to the model
    /// (default 50000; 0 = unlimited). Oversized results are cut and marked as
    /// truncated to keep context size and cost bounded.
    /// </summary>
    [PropertyGroup("MCP", 3)]
    public int MaxToolResultChars { get; set; } = 50_000;
}

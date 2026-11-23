using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Queries an LLM with pipeline data, optionally with MCP tools. Provider-agnostic by design; the
/// providers available today are listed in <see cref="LlmProvider"/>. Settings that fail their rule
/// (response format, temperature, tool-result cap) are refused when the pipeline definition is read.
/// The provider's endpoint is not a node setting: it comes with the API key from the AiConfiguration
/// (its optional sealed providerEndpoint: URL and key), so the key and the place it is sent to are
/// configured together.
/// </summary>
/// <remarks>
/// The configuration shape is lifted from the LlmQuery prototype by Philipp Schwaab and carries the
/// options of <c>AnthropicAiQuery@1</c>, except the inline API key and the old delegation flag.
/// </remarks>
[NodeName("LlmQuery", 1)]
public record LlmQueryNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>The system prompt of a node that sets none and uses no prompt template.</summary>
    public const string DefaultSystemPrompt =
        "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";

    /// <summary>The cap of <see cref="MaxToolResultChars"/> when none is set.</summary>
    public const int DefaultMaxToolResultChars = 50_000;

    private LlmProvider _provider = LlmProvider.Anthropic;
    private double? _temperature;
    private string _responseFormat = "json";
    private int _maxToolResultChars = DefaultMaxToolResultChars;

    // ---- Connection group ----

    /// <summary>
    /// LLM provider. Anthropic (default) uses the Anthropic Messages API.
    /// </summary>
    [PropertyGroup("Connection")]
    public LlmProvider Provider
    {
        get => _provider;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentException(
                    $"Unknown provider value '{(int)value}'. Use one of: {string.Join(", ", Enum.GetNames<LlmProvider>())}.",
                    nameof(value));
            }

            _provider = value;
        }
    }

    /// <summary>
    /// Well-known name of the AiConfiguration entity that holds the API key and, optionally, the
    /// sealed provider endpoint (providerEndpoint: URL and its own key; unset = https://api.anthropic.com with
    /// apiKey). An aiModel, maxTokens,
    /// temperature or mcpServerUrl set there wins over the node's own value.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? ApiKeyConfigurationName { get; set; }

    // ---- AI Configuration group ----

    /// <summary>
    /// Model identifier as the provider expects it (on Microsoft Foundry the deployment name). A prompt
    /// template's model and the AiConfiguration's aiModel win over it.
    /// </summary>
    [PropertyGroup("AI Configuration")]
    public string? Model { get; set; }

    /// <summary>
    /// Well-known name of a System.Ai/AiPromptTemplate that supplies system prompt, question, model,
    /// temperature and maxTokens. Inline question and systemPrompt override its texts.
    /// </summary>
    [PropertyGroup("AI Configuration", 1)]
    public string? PromptTemplateName { get; set; }

    /// <summary>
    /// The task or question for the model. Wins over the user template of a prompt template.
    /// </summary>
    [PropertyGroup("AI Configuration", 2, "textarea")]
    public string? Question { get; set; }

    /// <summary>
    /// System prompt setting role and rules for the model. Null without a prompt template = the default
    /// system prompt; an empty one sends no system prompt.
    /// </summary>
    [PropertyGroup("AI Configuration", 3, "textarea")]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Maximum tokens in the response (default 4096). A prompt template or the AiConfiguration wins.
    /// </summary>
    [PropertyGroup("AI Configuration", 4)]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature (0.0-1.0). Null (default) = not sent, the model's own default applies. A
    /// prompt template or the AiConfiguration wins. A model that rejects the temperature is asked once
    /// more without it, with a warning. A value outside 0.0-1.0 is refused.
    /// </summary>
    [PropertyGroup("AI Configuration", 5)]
    public double? Temperature
    {
        get => _temperature;
        set
        {
            if (value is not null and not (>= 0.0 and <= 1.0))
            {
                throw new ArgumentException($"temperature must be between 0.0 and 1.0, not {value}.", nameof(value));
            }

            _temperature = value;
        }
    }

    /// <summary>
    /// Sends the main content (path) as its own block with a prompt-cache breakpoint.
    /// </summary>
    [PropertyGroup("AI Configuration", 6)]
    public bool CacheSource { get; set; }

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
    /// Response format: "json" (default) or "text". Any other value is refused.
    /// </summary>
    [PropertyGroup("Output")]
    public string ResponseFormat
    {
        get => _responseFormat;
        set
        {
            if (value == null)
            {
                _responseFormat = "json";
                return;
            }

            if (!string.Equals(value, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown responseFormat '{value}'. Use json or text.", nameof(value));
            }

            _responseFormat = value;
        }
    }

    /// <summary>
    /// Whether to additionally write the raw model response to RawResponseOutputPath.
    /// </summary>
    [PropertyGroup("Output", 1)]
    public bool IncludeRawResponse { get; set; }

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
    /// (constrained decoding) and validated on the answer. Requires a model with
    /// structured-output support.
    /// </summary>
    [PropertyGroup("Output", 4, "code")]
    public string? JsonSchema { get; set; }

    /// <summary>
    /// Bounded repair for answers that violate the JSON schema: up to this many follow-up calls
    /// (default 1, capped at 2, 0 = off), each resending only the broken output plus
    /// the violations - never the original context.
    /// </summary>
    [PropertyGroup("Output", 5)]
    public int MaxJsonRepairAttempts { get; set; } = 1;

    /// <summary>
    /// Path the summed token usage of every call (tool rounds and repair calls included) is written to.
    /// </summary>
    [PropertyGroup("Output", 6, "jsonpath")]
    public string? UsageOutputPath { get; set; }

    // ---- Options group ----

    /// <summary>
    /// Whether to continue the pipeline if the LLM call fails.
    /// </summary>
    [PropertyGroup("Options")]
    public bool ContinueOnError { get; set; }

    // ---- MCP group ----

    /// <summary>
    /// MCP server URL; the AiConfiguration's mcpServerUrl wins over it. Tools are loaded from
    /// {mcpServerUrl}/{tenantId}/mcp. Without an identity (mcpServiceAccountConfigName) no MCP call is made.
    /// </summary>
    [PropertyGroup("MCP", 0)]
    public string? McpServerUrl { get; set; }

    /// <summary>
    /// Well-known name of the ServiceAccountConfiguration the MCP calls authenticate with.
    /// </summary>
    [PropertyGroup("MCP", 1)]
    public string? McpServiceAccountConfigName { get; set; }

    /// <summary>
    /// Whose identity the MCP calls run on: ServiceAccount (default) or Caller, which exchanges the
    /// caller's token for a delegated one (fail-closed; needs a trigger that carries a caller token).
    /// </summary>
    [PropertyGroup("MCP", 2)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.ServiceAccount;

    /// <summary>
    /// Optional tool allowlist. Null/empty = all tools of the MCP server are offered. Restricting tools
    /// cuts prompt tokens and keeps the model on task.
    /// </summary>
    [PropertyGroup("MCP", 3)]
    public string[]? McpToolNames { get; set; }

    /// <summary>
    /// Maximum tool-call rounds before the query fails (default 10).
    /// No effect when no MCP tools are configured.
    /// </summary>
    [PropertyGroup("MCP", 4)]
    public int MaxToolRounds { get; set; } = 10;

    /// <summary>
    /// Maximum characters (UTF-16 code units) of a single MCP tool result passed back to the model
    /// (default 50000, at least 1). Longer results are cut and marked as truncated.
    /// </summary>
    [PropertyGroup("MCP", 5)]
    public int? MaxToolResultChars
    {
        get => _maxToolResultChars;
        set
        {
            if (value is < 1)
            {
                throw new ArgumentException(
                    $"maxToolResultChars must be at least 1, not {value}.", nameof(value));
            }

            _maxToolResultChars = value ?? DefaultMaxToolResultChars;
        }
    }
}

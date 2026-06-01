using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for a provider-agnostic LLM Query node. Currently, supports:
/// <list type="bullet">
///   <item><description>OpenAICompatible</description></item>
///   <item><description>Anthropic</description></item>
/// </list>
/// </summary>
[NodeName("LlmQuery", 1)]
public record LlmQueryNodeConfiguration : SourceTargetPathNodeConfiguration
{
    
    // ---- Connection group ----

    /// <summary>
    /// LLM provider. v0.1 supports <see cref="LlmProvider.OpenAiCompatible"/>
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public LlmProvider Provider { get; set; } = LlmProvider.OpenAiCompatible;

    /// <summary>
    /// Base URL for OpenAI-compatible endpoints. Leave null for actual OpenAI
    /// cloud (api.openai.com). Examples:
    /// <list type="bullet">
    ///   <item><description>Ollama:    http://localhost:11434/v1/</description></item>
    ///   <item><description>vLLM:      http://vllm-server:8000/v1/</description></item>
    ///   <item><description>Cerebras:  https://api.cerebras.ai/v1/</description></item>
    ///   <item><description>Groq:      https://api.groq.com/openai/v1/</description></item>
    /// </list>
    /// The trailing slash is required. Ignored when Provider = Anthropic.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Name of the AiConfiguration entity in GlobalConfiguration to load the API
    /// key from. When set, the API key is read from the configuration entity and
    /// never exposed in the data context. Takes precedence over
    /// <see cref="ApiKey"/>.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string? ApiKeyConfigurationName { get; set; }

    /// <summary>
    /// LLM provider API key. Prefer <see cref="ApiKeyConfigurationName"/> in
    /// production to avoid exposing keys in pipeline definitions. For Ollama
    /// (no authentication), set this to any non-empty string (e.g. "ollama");
    /// the OpenAI SDK requires a non-null credential even when the backend
    /// ignores it.
    /// </summary>
    [PropertyGroup("Connection", 3, "password")]
    public string? ApiKey { get; set; }

    // ---- AI Configuration group ----

    /// <summary>
    /// Model identifier. Provider-specific:
    /// <list type="bullet">
    ///   <item><description>OpenAI:   gpt-4.1-mini, gpt-5-nano</description></item>
    ///   <item><description>Ollama:   qwen2.5:7b-instruct, llama3.3:8b, nemotron-3-nano:4b</description></item>
    ///   <item><description>Cerebras: gpt-oss-120b, llama-3.3-70b</description></item>
    /// </list>
    /// </summary>
    [PropertyGroup("AI Configuration", 0)]
    public string Model { get; set; } = "nemotron-3-nano:4b";

    /// <summary>
    /// The question/prompt to ask the AI about the data.
    /// </summary>
    [PropertyGroup("AI Configuration", 1, "textarea")]
    public required string Question { get; set; }

    /// <summary>
    /// System prompt to set the context for the AI.
    /// </summary>
    [PropertyGroup("AI Configuration", 2, "textarea")]
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";

    /// <summary>
    /// Maximum tokens for the response.
    /// </summary>
    [PropertyGroup("AI Configuration", 3)]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature (0.0 to 1.0). Lower = more deterministic.
    /// </summary>
    [PropertyGroup("AI Configuration", 4)]
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// Nucleus-sampling threshold. Optional; null = provider default.
    /// </summary>
    [PropertyGroup("AI Configuration", 5)]
    public float? TopP { get; set; }

    /// <summary>
    /// Top-K sampling cutoff. Optional; null = provider default. Note that
    /// OpenAI cloud silently ignores this; honoured by Anthropic, Ollama, and
    /// most other backends.
    /// </summary>
    [PropertyGroup("AI Configuration", 6)]
    public int? TopK { get; set; }

    /// <summary>
    /// Maximum call duration before cancellation fires. Default 90 seconds.
    /// Note: the OpenAI SDK's internal HttpClient ceiling is 100 seconds; if
    /// you need longer timeouts, the node must construct a custom transport
    /// (see docs/spike2_llmquery_fork.md Phase D).
    /// </summary>
    [PropertyGroup("AI Configuration", 7)]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    // ---- Paths group ----

    /// <summary>
    /// Optional context data paths to include in the query
    /// (e.g., ["$.ExtractedText", "$.DocumentMetadata"]).
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string[]? DataPaths { get; set; }

    /// <summary>
    /// Optional JSON path to conversation history array. Each entry should have
    /// "role" (user/assistant) and "content" fields. When set, previous messages
    /// are included for multi-turn conversations.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string? ConversationHistoryPath { get; set; }

    // ---- Output group ----

    /// <summary>
    /// Expected response format ("json" or "text").
    /// </summary>
    [PropertyGroup("Output", 0)]
    public string ResponseFormat { get; set; } = "json";

    /// <summary>
    /// Whether to include the raw AI response in the output.
    /// </summary>
    [PropertyGroup("Output", 1)]
    public bool IncludeRawResponse { get; set; } = false;

    /// <summary>
    /// Output path for the raw AI response (if IncludeRawResponse is true).
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? RawResponseOutputPath { get; set; }

    /// <summary>
    /// Sample JSON format for structured responses.
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

    // ---- Options group ----

    /// <summary>
    /// Whether to continue processing if the AI query fails.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool ContinueOnError { get; set; } = false;

    // MCP fields (McpServerUrl, MaxToolRounds, McpToolNames) intentionally
    // omitted from v0.1. Reintroduce when Anthropic provider lands in Spike 4.
    // See docs/production_fork_plan.md Phase 0.5 for context.
}
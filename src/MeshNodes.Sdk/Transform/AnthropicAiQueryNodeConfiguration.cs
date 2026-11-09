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
    /// Optional on the node when the <c>promptTemplateName</c> template supplies
    /// <c>DefaultModelId</c> or the referenced AiConfiguration supplies <c>aiModel</c>; those values
    /// take precedence, in that order. There is deliberately no hard-coded default model — a pinned model
    /// id goes out of date, so the model must be provided explicitly (node or AiConfiguration).
    /// </summary>
    [PropertyGroup("AI Configuration", 0)]
    public string? Model { get; set; }

    /// <summary>
    ///     Optional well-known name of a <c>System.Ai/AiPromptTemplate</c> entity with
    ///     <c>PromptPurpose</c> <c>KnowledgeGeneration</c> (AB#8498). The node loads its system prompt
    ///     (<c>SystemPromptFragment</c>, plus <c>FewShotExamples</c>), its question (<c>UserTemplate</c>)
    ///     and its model settings (<c>DefaultModelId</c>, <c>Temperature</c>, <c>MaxTokens</c>) from it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The template is handed over like the AiConfiguration of <c>apiKeyConfigurationName</c>:
    ///         the pipeline needs a <c>Uses</c> association to the template entity. The adapter caches
    ///         the configuration when the pipeline is registered, so a changed prompt takes effect after
    ///         the pipeline is redeployed.
    ///     </para>
    ///     <para>
    ///         Precedence: an inline <c>question</c> or <c>systemPrompt</c> overrides the
    ///         template's text. Model, temperature and max tokens come from the template first, then
    ///         from the AiConfiguration, then from the node properties. A named template that is not
    ///         handed over, is not an AiPromptTemplate or is not a KnowledgeGeneration template fails
    ///         the node with an error naming the template.
    ///     </para>
    /// </remarks>
    [PropertyGroup("AI Configuration", 1)]
    public string? PromptTemplateName { get; set; }

    /// <summary>
    ///     The question/prompt to ask the AI about the data. Required unless
    ///     <c>promptTemplateName</c> names a template with a <c>UserTemplate</c>; when set, it
    ///     overrides the template's <c>UserTemplate</c>.
    /// </summary>
    [PropertyGroup("AI Configuration", 2, "textarea")]
    public string? Question { get; set; }

    /// <summary>
    /// Optional context data paths to include in the query (e.g., ["$.ExtractedText", "$.DocumentMetadata"])
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string[]? DataPaths { get; set; }

    /// <summary>
    ///     The system prompt used when neither the node nor a prompt template sets one — the text
    ///     this node has always defaulted to.
    /// </summary>
    public const string DefaultSystemPrompt =
        "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";

    /// <summary>
    ///     System prompt to set the context for the AI. When set, it overrides the
    ///     <c>SystemPromptFragment</c> of the <c>promptTemplateName</c> template; when neither
    ///     is set, the old default text applies (the behaviour before AB#8498).
    /// </summary>
    [PropertyGroup("AI Configuration", 3, "textarea")]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Maximum tokens for the response. A MaxTokens value on the <c>promptTemplateName</c>
    /// template, then a maxTokens value on the referenced AiConfiguration entity take precedence;
    /// this property is the fallback.
    /// </summary>
    [PropertyGroup("AI Configuration", 4)]
    public int MaxTokens { get; set; } = 1000;

    /// <summary>
    /// Temperature for response generation (0.0 to 1.0). A Temperature value on the
    /// <c>promptTemplateName</c> template, then a temperature value on the referenced
    /// AiConfiguration entity take precedence; this property is the fallback.
    /// </summary>
    [PropertyGroup("AI Configuration", 5)]
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
    /// The AiConfiguration's McpServerUrl (via apiKeyConfigurationName) takes precedence.
    /// MCP is only called with an identity — <see cref="McpServiceAccountConfigName" /> or delegation to the
    /// caller. Without one the node skips MCP with one warning and runs the query without tools (AB#8685).
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? McpServerUrl { get; set; }

    /// <summary>
    /// Maximum number of tool use rounds to prevent infinite loops (default: 10)
    /// </summary>
    [PropertyGroup("AI Configuration", 6)]
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
    /// <c>{McpServerUrl}/{tenantId}/mcp</c> requests. Required for MCP tools: the MCP server enforces
    /// authentication (AB#4315), so without this setting (or delegation to the caller) the node does not
    /// call MCP at all — it warns once and runs the query without tools (AB#8685).
    /// </summary>
    [PropertyGroup("Connection", 3)]
    public string? McpServiceAccountConfigName { get; set; }

    /// <summary>
    ///     When <c>true</c>, the MCP calls run under the <b>end user's</b> identity instead of the
    ///     service account's: the node exchanges the caller's access token for a delegated one
    ///     (OctoMesh on-behalf-of grant, AB#5026/AB#5031) via
    ///     <see cref="McpServiceAccountConfigName" />, so the MCP server sees the user's own roles
    ///     and data permissions rather than the service account's full <c>octo_api</c> reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Requires a trigger that carries a caller token — today <c>FromHttpRequest@2</c> with
    ///         <c>allowAnonymous: false</c>. <b>Fail-closed:</b> with no caller token, or when the
    ///         delegated token cannot be acquired, the node fails; it never falls back to the service
    ///         account and never sends an unauthenticated MCP request. Channel assistants (Teams,
    ///         Signal, e-mail) have no caller and must leave this off.
    ///     </para>
    ///     <para>
    ///         Default <c>false</c> keeps the existing service-account behaviour byte for byte,
    ///         including its degrade-to-unauthenticated paths.
    ///     </para>
    /// </remarks>
    [PropertyGroup("Connection", 4)]
    public bool McpDelegateToCaller { get; set; }

    /// <summary>
    ///     The general execution-identity property (AB#5127). On this node it drives the MCP call
    ///     identity rather than a repository session: <see cref="NodeExecutionIdentity.Caller" />
    ///     delegates to the calling user (on-behalf-of, the same as legacy
    ///     <see cref="McpDelegateToCaller" /> = <c>true</c>); <see cref="NodeExecutionIdentity.ServiceAccount" />
    ///     and <see cref="NodeExecutionIdentity.System" /> use the service account's own token.
    /// </summary>
    /// <remarks>
    ///     The default is <see cref="NodeExecutionIdentity.ServiceAccount" /> — <b>not</b> the
    ///     <see cref="NodeExecutionIdentity.Caller" /> default of the data nodes — because the AI
    ///     node's historical behaviour (<see cref="McpDelegateToCaller" /> absent) is to call MCP as
    ///     the service account. <see cref="McpDelegateToCaller" /> stays supported as an alias:
    ///     setting either it or <c>identity: Caller</c> selects delegation, so both spellings keep
    ///     working.
    /// </remarks>
    [PropertyGroup("Connection", 5)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.ServiceAccount;

    /// <summary>
    ///     Whether this node's MCP calls run on the caller's identity (AB#5127 alias resolution):
    ///     <c>true</c> when either the legacy <see cref="McpDelegateToCaller" /> flag is set or
    ///     <see cref="Identity" /> is <see cref="NodeExecutionIdentity.Caller" />. Either "caller"
    ///     spelling wins, so a pipeline can use whichever it likes.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DelegatesToCaller =>
        McpDelegateToCaller || Identity == NodeExecutionIdentity.Caller;

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

    /// <summary>
    ///     Optional JSON Schema (as a JSON string) the answer must satisfy; only used with
    ///     <c>responseFormat: json</c> (AB#8498). The node requests constrained decoding with the schema,
    ///     validates the parsed answer against it and, if invalid, runs at most
    ///     <c>maxJsonRepairAttempts</c> repair calls that resend only the broken answer, the errors and
    ///     the schema. An answer that stays invalid fails the node with an error naming the violated
    ///     path(s). Without <c>jsonSchema</c> the node behaves as before.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         When set, the node (1) asks the API for constrained decoding with the schema
    ///         (<c>output_config.format</c>, structured outputs), (2) validates the parsed answer
    ///         against the full schema after the deterministic repairs (JSON extraction from prose,
    ///         mechanical repair of naked quotes, trailing commas and unclosed brackets, repeated
    ///         keys), and (3) when the answer still does not parse or violates the schema, runs at
    ///         most <see cref="MaxJsonRepairAttempts" /> repair calls that send only the broken answer,
    ///         the errors and the schema back — never the original prompt or context. If the answer
    ///         is still invalid the node fails with an error that names the violated path(s);
    ///         <see cref="ContinueOnError" /> applies as usual.
    ///     </para>
    ///     <para>
    ///         Schema keywords the API does not support for constrained decoding (numeric and string
    ///         length constraints, <c>pattern</c>, array size constraints) are removed from the copy
    ///         sent to the API and enforced by the client-side validation only.
    ///     </para>
    ///     <para>
    ///         Without <c>jsonSchema</c> the node behaves exactly as before: no constrained decoding,
    ///         no validation, no repair calls.
    ///     </para>
    /// </remarks>
    [PropertyGroup("Output", 4, "code")]
    public string? JsonSchema { get; set; }

    /// <summary>
    ///     Upper bound of repair calls when a <c>jsonSchema</c> answer does not parse or does not
    ///     validate (default 1, hard-capped at 2, 0 = no repair call). Each call resends only the
    ///     broken answer, the parser or validation errors and the schema. Ignored without
    ///     <c>jsonSchema</c>.
    /// </summary>
    [PropertyGroup("Output", 5)]
    public int MaxJsonRepairAttempts { get; set; } = 1;

    /// <summary>
    ///     Optional JSONPath where the node writes what its API calls used (AB#8658): <c>model</c>,
    ///     <c>calls</c>, <c>inputTokens</c>, <c>outputTokens</c>, <c>cacheReadInputTokens</c>,
    ///     <c>cacheCreationInputTokens</c> and <c>estimatedCostUsd</c> (with <c>priceListDate</c>), summed
    ///     over every call of the execution — tool rounds and JSON repair calls included. Not set = nothing
    ///     is written (the behaviour before AB#8658).
    /// </summary>
    /// <remarks>
    ///     <c>inputTokens</c> is the uncached remainder; the full prompt size is the sum of the three input
    ///     fields. <c>estimatedCostUsd</c> uses the adapter's built-in Anthropic list prices
    ///     (first-party API, 5-minute cache writes) and is <c>null</c> for a model the table does not know.
    ///     It is an estimate, not the invoice. With <c>continueOnError</c> the usage is written for a failed
    ///     query too, since the calls were made and paid.
    /// </remarks>
    [PropertyGroup("Output", 6, "jsonpath")]
    public string? UsageOutputPath { get; set; }

    /// <summary>
    ///     When <c>true</c>, the main content of the user message (the value at <c>path</c>) is sent as its
    ///     own text block with an <c>ephemeral</c> prompt-cache breakpoint, followed by a second block with
    ///     the <c>dataPaths</c> context, the caller and the question (AB#8658). Calls whose request is
    ///     identical up to the end of that block (same tools, same system prompt, same source) read it from
    ///     the cache (<c>cacheReadInputTokens</c>) within its 5-minute lifetime: every MCP tool round of one
    ///     execution, and repeated runs of the same node over the same source (e.g. reviewer rounds that only
    ///     change the draft in <c>dataPaths</c>). Default <c>false</c>: the user message is a single string,
    ///     as before; the text the model reads is the same either way.
    /// </summary>
    /// <remarks>
    ///     A cache write costs 1.25× input once; a hit costs about 0.1× (less on newer models). It pays off
    ///     from the second call over the same source. Prefixes below the model's minimum cacheable length
    ///     (512–4096 tokens) are silently not cached. The cache is a prefix match (tools, then system prompt,
    ///     then messages): two nodes with different system prompts never share the cached source. The system
    ///     prompt and the tool list are cached regardless of this setting.
    /// </remarks>
    [PropertyGroup("AI Configuration", 7)]
    public bool CacheSource { get; set; }
}
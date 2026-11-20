using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     What one LLM query node asks of the <see cref="LlmQueryEngine" />: the values of the node's
///     configuration, mapped by the node, and the few texts that differ per node. The engine reads no
///     node configuration type, so every node that maps to this record runs the same code.
/// </summary>
internal sealed record LlmQueryOptions
{
    /// <summary>The node as pipeline authors write it (e.g. <c>AnthropicAiQuery@1</c>); part of warnings.</summary>
    public required string NodeDisplayName { get; init; }

    /// <summary>The <c>clientInfo.name</c> the node announces in the MCP <c>initialize</c> request.</summary>
    public required string McpClientName { get; init; }

    /// <summary>The system prompt of a node that sets none and uses no prompt template.</summary>
    public required string DefaultSystemPrompt { get; init; }

    /// <summary>JSON path of the main content.</summary>
    public string Path { get; init; } = "$";

    /// <summary>JSON path the processed answer is written to.</summary>
    public string TargetPath { get; init; } = "$";

    /// <summary>How the answer is written to <see cref="TargetPath" />.</summary>
    public TargetValueWriteModes TargetValueWriteMode { get; init; } = TargetValueWriteModes.Overwrite;

    /// <summary>The value kind the answer is written as.</summary>
    public ValueKinds TargetValueKind { get; init; } = ValueKinds.Simple;

    /// <summary>Whether the answer extends or replaces the document.</summary>
    public DocumentModes DocumentMode { get; init; } = DocumentModes.Extend;

    /// <summary>Name of the AiConfiguration that holds the API key and, optionally, model, limits and MCP URL.</summary>
    public string? ApiKeyConfigurationName { get; init; }

    /// <summary>API key set on the node itself; used when the AiConfiguration yields none.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Model set on the node; a prompt template and the AiConfiguration win over it.</summary>
    public string? Model { get; init; }

    /// <summary>Name of the AiPromptTemplate that supplies prompt, model and limits.</summary>
    public string? PromptTemplateName { get; init; }

    /// <summary>The question; wins over the template's user template.</summary>
    public string? Question { get; init; }

    /// <summary>JSON paths whose values are added to the prompt as additional context.</summary>
    public string[]? DataPaths { get; init; }

    /// <summary>System prompt set on the node; an empty one sends no system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Output-token limit set on the node.</summary>
    public int MaxTokens { get; init; }

    /// <summary>Temperature set on the node.</summary>
    public double Temperature { get; init; }

    /// <summary><c>json</c> or <c>text</c>.</summary>
    public string ResponseFormat { get; init; } = "json";

    /// <summary>Whether the unprocessed answer is written as well.</summary>
    public bool IncludeRawResponse { get; init; }

    /// <summary>JSON path of the unprocessed answer.</summary>
    public string? RawResponseOutputPath { get; init; }

    /// <summary>Whether a failed query is logged and the pipeline continues.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>MCP server URL set on the node; the AiConfiguration's URL wins over it.</summary>
    public string? McpServerUrl { get; init; }

    /// <summary>How many tool rounds the model may take.</summary>
    public int MaxToolRounds { get; init; }

    /// <summary>The MCP tools the model may see; all of the server when empty.</summary>
    public string[]? McpToolNames { get; init; }

    /// <summary>Name of the ServiceAccountConfiguration the MCP calls authenticate with.</summary>
    public string? McpServiceAccountConfigName { get; init; }

    /// <summary>The node's own delegation flag; one of the settings that promise tools to the model.</summary>
    public bool McpDelegateToCaller { get; init; }

    /// <summary>Whether the MCP calls run on the caller's identity, whichever spelling the node used.</summary>
    public bool DelegatesToCaller { get; init; }

    /// <summary>JSON path of the earlier turns of the conversation.</summary>
    public string? ConversationHistoryPath { get; init; }

    /// <summary>The JSON sample shown to the model when no schema is set.</summary>
    public string JsonFormatSample { get; init; } = string.Empty;

    /// <summary>The JSON Schema of the answer (guaranteed JSON).</summary>
    public string? JsonSchema { get; init; }

    /// <summary>How many repair calls may follow an answer that violates the schema.</summary>
    public int MaxJsonRepairAttempts { get; init; }

    /// <summary>JSON path the token usage is written to.</summary>
    public string? UsageOutputPath { get; init; }

    /// <summary>Whether the main content carries a prompt-cache breakpoint.</summary>
    public bool CacheSource { get; init; }

    /// <summary>
    ///     The longest tool result, in UTF-16 code units, that enters the conversation uncut.
    /// </summary>
    public int MaxToolResultChars { get; init; } = LlmQueryEngine.DefaultMaxToolResultChars;
}

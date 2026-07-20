using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for a deterministic MCP (Model Context Protocol) tool call.
/// Unlike <c>LlmQuery@1</c> — where the model decides which tool to call —
/// this node invokes a single named tool on a single MCP server directly, with
/// arguments taken from the pipeline data or supplied inline. No LLM is
/// involved, so the call is deterministic, auditable and token-free.
/// <para>
/// Server resolution, auth (Bearer / AdditionalHeaders) and transport selection
/// reuse the same <c>System.Communication/McpConfiguration</c> entities and the
/// shared resolver that <c>LlmQuery@1</c> uses, so a server configured once works
/// for both the agentic and the deterministic path.
/// </para>
/// </summary>
[NodeName("McpToolCall", 1)]
public record McpToolCallNodeConfiguration : TargetPathNodeConfiguration
{
    // ---- Connection group ----

    /// <summary>
    /// Well-known name of the <c>System.Communication/McpConfiguration</c> entity
    /// whose MCP server hosts the tool. Resolved from <c>GlobalConfiguration</c> at
    /// pipeline-execution time. The configuration must be linked to the pipeline via
    /// the <c>Uses</c> association so it ships in <c>GlobalConfiguration</c>; an
    /// unknown name logs a warning and the node passes through without calling.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string McpConfigurationName { get; set; }

    /// <summary>
    /// Name of the tool to invoke, exactly as advertised by the MCP server
    /// (e.g. <c>web_search_exa</c>, <c>deep_researcher_start</c>,
    /// <c>get_current_time</c>). Use <c>LlmQuery@1</c>'s startup log
    /// ("MCP server '…' contributed N tool(s): …") to discover the available names.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string ToolName { get; set; }

    // ---- Arguments group ----

    /// <summary>
    /// Inline JSON object with the tool arguments, e.g.
    /// <c>{ "query": "latest .NET release", "numResults": 5 }</c>. Takes
    /// precedence over <see cref="ArgumentsPath"/>. Leave null when the tool needs
    /// no arguments or when the arguments come from the pipeline data via
    /// <see cref="ArgumentsPath"/>.
    /// </summary>
    [PropertyGroup("Arguments", 0, "code")]
    public string? Arguments { get; set; }

    /// <summary>
    /// JSON path to an object in the pipeline data context to use as the tool
    /// arguments (e.g. <c>$.body.toolArgs</c>). Used only when
    /// <see cref="Arguments"/> is null. The value at the path is serialized to JSON
    /// and passed verbatim as the argument object.
    /// </summary>
    [PropertyGroup("Arguments", 1, "jsonpath")]
    public string? ArgumentsPath { get; set; }

    // ---- Options group ----

    /// <summary>
    /// Maximum call duration in seconds before cancellation fires. Default 90.
    /// Raise for slow remote tools (large web crawls, document fetches); a
    /// long-running async tool (e.g. <c>deep_researcher</c>) should instead be
    /// driven by a scheduled poll pipeline rather than a single blocking call.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Whether to continue the pipeline if the tool call fails to execute
    /// (connection error, timeout is always fatal). Note: a tool that returns an
    /// MCP error result (<c>isError: true</c>, e.g. an invalid API key) is not a
    /// failure here — the error result is still written to <see cref="TargetPathNodeConfiguration.TargetPath"/>
    /// so a downstream <c>If@1</c> can branch on it — but a warning is logged.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool ContinueOnError { get; set; } = false;
}

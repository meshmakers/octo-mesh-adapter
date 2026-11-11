using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
///     Calls one tool on the tenant's OctoMesh MCP service deterministically — no language model
///     decides anything (AB#8658). Typical use: after an <c>AnthropicAiQuery@1</c> step has produced
///     schema-valid JSON, the pipeline writes it through an MCP tool such as
///     <c>knowledge_upsert_page</c>, so the tool's own guards (content guard, versioning) apply.
/// </summary>
/// <remarks>
///     <para>
///         <b>Allowlist.</b> <c>allowedTools</c> is required and names every tool the node may
///         call. The tool to call (<c>toolName</c>, or the value at <c>toolNamePath</c>)
///         must be one of them (exact, case-sensitive match); otherwise the node fails before any token
///         is requested or any network call is made. <c>continueOnError</c> does not apply to
///         that failure.
///     </para>
///     <para>
///         <b>Identity.</b> The call always runs as the service account of
///         <c>mcpServiceAccountConfigName</c> (a <c>System.Communication/ServiceAccountConfiguration</c>,
///         e.g. <c>knowledge-curator</c>). Fail-closed: without a token the node fails; it never sends an
///         unauthenticated request. Credentials never appear in the pipeline definition.
///     </para>
///     <para>
///         <b>Endpoint.</b> The MCP service URL comes from the <c>McpServerUrl</c> of the
///         <c>AiConfiguration</c> named by <c>aiConfigurationName</c> (the per-cluster value, handed
///         over through the pipeline's <c>Uses</c> association), else from <c>mcpServerUrl</c>. The
///         tenant is appended: <c>{url}/{tenantId}/mcp</c>.
///     </para>
/// </remarks>
[NodeName("McpToolCall", 1)]
public record McpToolCallNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    ///     The tools this node may call (exact MCP tool names, e.g. <c>["knowledge_upsert_page"]</c>).
    ///     Required and non-empty: there is no "call any tool" mode.
    /// </summary>
    [PropertyGroup("Tool", 0)]
    public string[] AllowedTools { get; set; } = [];

    /// <summary>
    ///     The tool to call. Exactly one of <c>toolName</c> and <c>toolNamePath</c> is set.
    /// </summary>
    [PropertyGroup("Tool", 1)]
    public string? ToolName { get; set; }

    /// <summary>
    ///     JSONPath to a string in the data context naming the tool to call (e.g. chosen by an earlier
    ///     step). The value must still be in <c>allowedTools</c>.
    /// </summary>
    [PropertyGroup("Tool", 2, "jsonpath")]
    public string? ToolNamePath { get; set; }

    /// <summary>
    ///     JSONPath to a JSON object in the data context that is sent as the tool's arguments. When not
    ///     set, the tool is called with an empty argument object. A path that does not resolve to an
    ///     object fails the node.
    /// </summary>
    [PropertyGroup("Tool", 3, "jsonpath")]
    public string? ArgumentsPath { get; set; }

    /// <summary>
    ///     Name of a top-level tool argument that receives the id of the current pipeline execution (a GUID
    ///     string), e.g. <c>runRef</c> for <c>knowledge_upsert_page</c>, so the written page version references
    ///     the exact run (AB#8703). The value replaces an argument of the same name from <c>argumentsPath</c>.
    ///     Not set = the arguments are sent unchanged.
    /// </summary>
    [PropertyGroup("Tool", 4)]
    public string? ExecutionIdArgument { get; set; }

    /// <summary>
    ///     Well-known name of the <c>System.Communication/AiConfiguration</c> whose <c>McpServerUrl</c> is
    ///     used (recommended; the pipeline needs a <c>Uses</c> association to it). Takes precedence over
    ///     <c>mcpServerUrl</c>.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string? AiConfigurationName { get; set; }

    /// <summary>
    ///     Fallback MCP service URL (e.g. <c>https://localhost:5017</c> for local development) when no
    ///     <c>aiConfigurationName</c> supplies one.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string? McpServerUrl { get; set; }

    /// <summary>
    ///     Well-known name of the <c>System.Communication/ServiceAccountConfiguration</c> the tool call
    ///     runs as. Required.
    /// </summary>
    [PropertyGroup("Connection", 2)]
    public string? McpServiceAccountConfigName { get; set; }

    /// <summary>
    ///     Maximum duration of the MCP calls in seconds (default 90). Must be positive.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    ///     Whether the pipeline continues when the tool call fails (MCP error, tool error result,
    ///     unreachable server). Configuration errors and allowlist violations always fail the node.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool ContinueOnError { get; set; }
}

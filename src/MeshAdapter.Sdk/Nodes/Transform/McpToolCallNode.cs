using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
///     <c>McpToolCall@1</c> (AB#8658): one deterministic call of an allowlisted tool on the tenant's MCP
///     service, as a configured service account. See <see cref="McpToolCallNodeConfiguration" />.
/// </summary>
/// <remarks>
///     Order of the checks is part of the contract (tests <c>McpToolCallNodeTests</c>): configuration and
///     allowlist are validated first, then the arguments, then the endpoint — all before a token is
///     requested or any request is sent. Only the call itself is subject to <c>continueOnError</c>.
/// </remarks>
[NodeConfiguration(typeof(McpToolCallNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class McpToolCallNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    IServiceAccountTokenService serviceAccountTokenService)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<McpToolCallNodeConfiguration>();

        // Everything up to the URL is deterministic and free: fail before any network call.
        ValidateConfiguration(config, nodeContext);
        var toolName = ResolveToolName(config, dataContext, nodeContext);
        EnsureAllowed(config, toolName, nodeContext);
        var arguments = ResolveArguments(config, dataContext, nodeContext);
        var endpoint = ResolveEndpoint(config, etlContext, nodeContext);

        string? accessToken = null;
        try
        {
            accessToken = await serviceAccountTokenService.AcquireServiceAccountTokenAsync(
                etlContext.TenantRepository, config.McpServiceAccountConfigName!);
            if (string.IsNullOrEmpty(accessToken))
            {
                // Fail-closed: a write identity is never replaced by "no identity".
                throw new McpToolCallException(
                    $"No access token could be acquired via ServiceAccountConfiguration " +
                    $"'{config.McpServiceAccountConfigName}' (see the adapter log for the reason). The tool " +
                    $"'{toolName}' was not called; MCP calls are never sent unauthenticated.");
            }

            // AB#5538: the token is masked in every diagnostic output of this execution.
            nodeContext.RegisterSecret(accessToken);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var client = new McpToolClient(httpClientFactory.CreateClient(McpToolClient.HttpClientName), endpoint,
                accessToken);

            nodeContext.Debug($"Calling MCP tool '{toolName}' as '{config.McpServiceAccountConfigName}'");
            var result = await client.CallToolAsync(toolName, arguments, timeout.Token);

            dataContext.Set(config.TargetPath, result, config.DocumentMode, config.TargetValueKind,
                config.TargetValueWriteMode);
            nodeContext.Info($"MCP tool '{toolName}' completed");
        }
        catch (Exception ex) when (ex is McpToolCallException or HttpRequestException or OperationCanceledException
                                       or JsonException)
        {
            var message = Redact(ex is OperationCanceledException
                ? $"MCP tool '{toolName}' did not answer within {config.TimeoutSeconds} s."
                : ex.Message, accessToken);
            if (!config.ContinueOnError)
            {
                // The inner exception is dropped on purpose: its message is already redacted into ours,
                // and an HttpRequestException could carry request details.
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext,
                    new McpToolCallException(message));
            }

            nodeContext.Error($"MCP tool call failed: {message}");
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>Configuration errors: never subject to <c>continueOnError</c>.</summary>
    internal static void ValidateConfiguration(McpToolCallNodeConfiguration config, INodeContext nodeContext)
    {
        if (config.AllowedTools is not { Length: > 0 } || config.AllowedTools.Any(string.IsNullOrWhiteSpace))
        {
            throw ConfigurationError(nodeContext,
                "'allowedTools' is required and must list the MCP tool names this node may call.");
        }

        var hasName = !string.IsNullOrWhiteSpace(config.ToolName);
        var hasPath = !string.IsNullOrWhiteSpace(config.ToolNamePath);
        if (hasName == hasPath)
        {
            throw ConfigurationError(nodeContext, "Set exactly one of 'toolName' and 'toolNamePath'.");
        }

        if (string.IsNullOrWhiteSpace(config.McpServiceAccountConfigName))
        {
            throw ConfigurationError(nodeContext,
                "'mcpServiceAccountConfigName' is required: the tool call runs as that service account.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            throw ConfigurationError(nodeContext, "'timeoutSeconds' must be positive.");
        }
    }

    internal static string ResolveToolName(McpToolCallNodeConfiguration config, IDataContext dataContext,
        INodeContext nodeContext)
    {
        if (!string.IsNullOrWhiteSpace(config.ToolName))
        {
            return config.ToolName;
        }

        var name = dataContext.GetKind(config.ToolNamePath!) == DataKind.String
            ? dataContext.Get<string>(config.ToolNamePath!)
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ConfigurationError(nodeContext,
                $"'toolNamePath' {config.ToolNamePath} does not resolve to a tool name (string).");
        }

        return name;
    }

    /// <summary>The allowlist check: exact, case-sensitive — MCP tool names are case-sensitive.</summary>
    internal static void EnsureAllowed(McpToolCallNodeConfiguration config, string toolName,
        INodeContext nodeContext)
    {
        if (!config.AllowedTools.Contains(toolName, StringComparer.Ordinal))
        {
            throw ConfigurationError(nodeContext,
                $"MCP tool '{toolName}' is not in this node's allowedTools " +
                $"[{string.Join(", ", config.AllowedTools)}]. The tool was not called.");
        }
    }

    internal static JsonObject ResolveArguments(McpToolCallNodeConfiguration config, IDataContext dataContext,
        INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(config.ArgumentsPath))
        {
            return new JsonObject();
        }

        if (dataContext.GetKind(config.ArgumentsPath) != DataKind.Object ||
            dataContext.Get<JsonNode>(config.ArgumentsPath) is not JsonObject arguments)
        {
            throw ConfigurationError(nodeContext,
                $"'argumentsPath' {config.ArgumentsPath} does not resolve to a JSON object. The tool was not called.");
        }

        // Detached copy: the request must not hold on to (or mutate) the data context's tree.
        return (JsonObject)arguments.DeepClone();
    }

    internal static Uri ResolveEndpoint(McpToolCallNodeConfiguration config, IMeshEtlContext etlContext,
        INodeContext nodeContext)
    {
        string? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(config.AiConfigurationName))
        {
            if (etlContext.GlobalConfiguration.IsDefined(config.AiConfigurationName) &&
                etlContext.GlobalConfiguration.GetRawJson(config.AiConfigurationName) is { } rawJson)
            {
                baseUrl = JsonNode.Parse(rawJson)?["mcpServerUrl"]?.GetValue<string>();
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                nodeContext.Warning(
                    $"AiConfiguration '{config.AiConfigurationName}' not found or has no McpServerUrl. Ensure the " +
                    "pipeline has a 'Uses' association to it; falling back to the node's mcpServerUrl.");
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = config.McpServerUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate($"{baseUrl.TrimEnd('/')}/{etlContext.TenantId}/mcp", UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw ConfigurationError(nodeContext,
                "No usable MCP server URL: set McpServerUrl on the AiConfiguration named by 'aiConfigurationName' " +
                "or 'mcpServerUrl' on the node (absolute http(s) URL).");
        }

        return endpoint;
    }

    /// <summary>Defence in depth: the token must never reach a message, even if a server echoed it.</summary>
    internal static string Redact(string message, string? accessToken)
    {
        return string.IsNullOrEmpty(accessToken) ? message : message.Replace(accessToken, "***", StringComparison.Ordinal);
    }

    private static Exception ConfigurationError(INodeContext nodeContext, string reason)
    {
        return MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, new McpToolCallException(reason));
    }
}

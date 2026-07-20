using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using ModelContextProtocol.Client;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Invokes a single MCP (Model Context Protocol) tool directly — no LLM in the
/// loop. The server is resolved from <c>GlobalConfiguration</c> by name and the
/// transport (stdio / SSE / HTTP, with Bearer / AdditionalHeaders auth) is built
/// by the shared <see cref="McpServerResolver"/>, identical to the agentic
/// <c>LlmQuery@1</c> path. The tool's structured result is written to
/// <c>TargetPath</c> so the rest of the pipeline (If, Switch, notifications,
/// persistence) can act on it — turning any MCP server into a deterministic,
/// schedulable pipeline step.
/// </summary>
[NodeConfiguration(typeof(McpToolCallNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class McpToolCallNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IServiceAccountTokenService serviceAccountTokenService)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<McpToolCallNodeConfiguration>();

        // Bounds the connect + tool-call duration; also honours upstream interrupts.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            if (string.IsNullOrWhiteSpace(config.McpConfigurationName))
            {
                throw new ArgumentException("McpConfigurationName is required", nameof(config.McpConfigurationName));
            }

            if (string.IsNullOrWhiteSpace(config.ToolName))
            {
                throw new ArgumentException("ToolName is required", nameof(config.ToolName));
            }

            if (string.IsNullOrWhiteSpace(config.TargetPath))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(
                    nodeContext, nameof(config.TargetPath));
            }

            // Resolve the single named server. Resolve() already logs a warning for an
            // unknown name; in that case we have nothing to call, so pass through.
            var servers = McpServerResolver
                .Resolve([config.McpConfigurationName], etlContext, nodeContext);
            if (servers.Count == 0)
            {
                await next(dataContext, nodeContext);
                return;
            }

            // Acquire a client-credentials bearer when the configuration references a
            // ServiceAccountConfiguration (AB#4315) — identical path to LlmQuery@1.
            servers = await McpServerResolver.ApplyServiceAccountTokensAsync(
                servers, serviceAccountTokenService, etlContext, nodeContext, ct);
            var server = servers[0];

            var arguments = ParseArguments(config, dataContext, nodeContext);

            var transport = McpServerResolver.BuildTransport(server);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            nodeContext.Info(
                $"Calling MCP tool '{config.ToolName}' on '{server.Name}'" +
                (arguments is { Count: > 0 } ? $" with {arguments.Count} argument(s)" : " with no arguments"));

            var result = await client.CallToolAsync(config.ToolName, arguments, cancellationToken: ct);

            // Serialize the whole CallToolResult (content blocks + isError) so downstream
            // nodes get the full structured payload. Defensive fallback to string form if
            // the SDK type can't be serialized with the runtime's STJ options.
            JsonNode? resultNode;
            try
            {
                resultNode = JsonSerializer.SerializeToNode(result, SystemTextJsonOptions.Default);
            }
            catch (Exception serEx)
            {
                nodeContext.Warning(
                    $"Could not serialize MCP tool result to JSON " +
                    $"({serEx.GetType().Name}: {serEx.Message}); storing string form.");
                resultNode = JsonValue.Create(result.ToString());
            }

            // An MCP error result (isError: true — e.g. an invalid API key) is data, not a
            // pipeline failure: write it so a downstream If@1 can branch on it, but warn.
            if (result.IsError == true)
            {
                nodeContext.Warning(
                    $"MCP tool '{config.ToolName}' on '{server.Name}' returned an error result " +
                    "(isError=true); see the stored result for details.");
            }

            var resultText = resultNode?.ToJsonString() ?? "<null>";
            const int maxResultLogLength = 500;
            if (resultText.Length > maxResultLogLength)
            {
                resultText = resultText[..maxResultLogLength] + $"… [{resultText.Length} chars total]";
            }
            nodeContext.Debug($"MCP tool '{config.ToolName}' result: {resultText}");

            dataContext.Set(
                config.TargetPath,
                resultNode,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode);

            nodeContext.Info($"McpToolCall '{config.ToolName}' completed successfully");
        }
        catch (OperationCanceledException oce) when (timeoutCts.IsCancellationRequested)
        {
            // Our TimeoutSeconds budget elapsed (not an upstream interrupt). Timeout is
            // always fatal — a long-running async tool belongs in a scheduled poll
            // pipeline, not a single blocking call.
            nodeContext.Error(
                $"McpToolCall '{config.ToolName}' timed out after {config.TimeoutSeconds}s. " +
                "Increase TimeoutSeconds, or for long-running async tools drive them via a " +
                "scheduled poll pipeline instead of one blocking call.");
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, oce);
        }
        catch (OperationCanceledException)
        {
            // Genuine upstream cancellation (pipeline shutdown / caller abort) — let the
            // runtime see it; ContinueOnError does not apply to deliberate cancellation.
            throw;
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during McpToolCall ({ex.GetType().Name}): {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Builds the tool argument object from either the inline <c>Arguments</c> JSON
    /// (preferred) or the value at <c>ArgumentsPath</c> in the pipeline data.
    /// Returns null when neither is set (tools that take no arguments). Values
    /// materialize as <see cref="JsonElement"/>, which the MCP SDK re-serializes
    /// into the tool call.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? ParseArguments(
        McpToolCallNodeConfiguration config, IDataContext dataContext, INodeContext nodeContext)
    {
        string? rawJson = null;

        if (!string.IsNullOrWhiteSpace(config.Arguments))
        {
            rawJson = config.Arguments;
        }
        else if (!string.IsNullOrWhiteSpace(config.ArgumentsPath)
                 && dataContext.GetKind(config.ArgumentsPath) is not DataKind.Undefined)
        {
            var value = dataContext.Get<object?>(config.ArgumentsPath);
            rawJson = JsonSerializer.Serialize(value, SystemTextJsonOptions.Default);
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(rawJson, SystemTextJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            nodeContext.Warning(
                $"Could not parse tool arguments as a JSON object ({ex.Message}); " +
                "calling the tool with no arguments.");
            return null;
        }
    }
}

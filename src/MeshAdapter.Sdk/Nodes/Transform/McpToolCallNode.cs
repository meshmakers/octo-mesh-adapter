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
    /// <summary>Transport factory for the resolved server; tests substitute an in-process transport.</summary>
    internal Func<McpServerResolver.McpServerConfig, IClientTransport> TransportFactory { get; init; } =
        McpServerResolver.BuildTransport;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<McpToolCallNodeConfiguration>();

        // Invalid configuration fails before any work — outside the try on purpose: ContinueOnError
        // is for runtime failures, not for a persisted TimeoutSeconds that can never work (a negative
        // value makes the CancellationTokenSource throw, 0 hands the tool an already-cancelled token).
        if (config.TimeoutSeconds <= 0)
        {
            throw MeshAdapterPipelineExecutionException.ProcessingError(
                nodeContext,
                new ArgumentOutOfRangeException(nameof(config.TimeoutSeconds), config.TimeoutSeconds,
                    "TimeoutSeconds must be a positive number of seconds."));
        }

        // Required settings are configuration errors too: a blank server or tool name can never
        // work, so ContinueOnError must not turn the node into a silent pass-through.
        if (string.IsNullOrWhiteSpace(config.McpConfigurationName))
        {
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext,
                new ArgumentException("McpConfigurationName is required", nameof(config.McpConfigurationName)));
        }

        if (string.IsNullOrWhiteSpace(config.ToolName))
        {
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext,
                new ArgumentException("ToolName is required", nameof(config.ToolName)));
        }

        if (string.IsNullOrWhiteSpace(config.TargetPath))
        {
            throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(nodeContext, nameof(config.TargetPath));
        }

        // An unknown McpConfiguration is a configuration error: fail before any work, regardless
        // of ContinueOnError, instead of passing through as if the call had happened.
        var servers = McpServerResolver
            .Resolve([config.McpConfigurationName], etlContext, nodeContext);
        if (servers.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext,
                new InvalidOperationException(
                    $"McpConfiguration '{config.McpConfigurationName}' is not present in GlobalConfiguration " +
                    "or has no usable server definition."));
        }

        // Bounds the connect + tool-call duration; also honours upstream interrupts.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            // Acquire a client-credentials bearer when the configuration references a
            // ServiceAccountConfiguration — identical path to LlmQuery@1.
            servers = await McpServerResolver.ApplyServiceAccountTokensAsync(
                servers, serviceAccountTokenService, etlContext, nodeContext, ct);
            var server = servers[0];

            var arguments = ParseArguments(config, dataContext, nodeContext);

            // The client disposes only the connected session; an HTTP transport owns its HttpClient
            // and has to be disposed separately, after the client.
            var transport = TransportFactory(server);
            await using var transportLifetime = new TransportLifetime(transport);
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

            // Metadata only: a tool result is unrestricted third-party output and may carry
            // credentials or PII, which must not land in the execution log. The payload itself
            // goes to TargetPath, where the pipeline author controls what happens to it.
            nodeContext.Debug(
                $"MCP tool '{config.ToolName}' returned {result.Content.Count} content block(s)" +
                $"{(result.StructuredContent is not null ? " with structured content" : string.Empty)}, " +
                $"isError={result.IsError == true}");

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
        else if (!string.IsNullOrWhiteSpace(config.ArgumentsPath))
        {
            // A configured argument source that yields nothing must not become an argumentless call.
            if (dataContext.GetKind(config.ArgumentsPath) is DataKind.Undefined)
            {
                throw new ArgumentException(
                    $"'{nameof(config.ArgumentsPath)}' ({config.ArgumentsPath}) resolves to no value in the pipeline data; " +
                    "the tool is not called without the configured arguments.");
            }

            var value = dataContext.Get<object?>(config.ArgumentsPath);
            rawJson = JsonSerializer.Serialize(value, SystemTextJsonOptions.Default);
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        // A tool invoked without its arguments does not degrade safely: a search tool runs with no
        // query, a scoped tool runs unscoped, and the pipeline stores a wrong result as a success.
        // Fail instead and let ContinueOnError decide (the caller's generic handler wraps this).
        Dictionary<string, object?>? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawJson, SystemTextJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Tool arguments are not a JSON object ({ex.Message}). Check '{nameof(config.Arguments)}' or " +
                $"the value at '{nameof(config.ArgumentsPath)}' ({config.ArgumentsPath}).", ex);
        }

        return arguments ?? throw new ArgumentException(
            $"Tool arguments resolved to JSON null. Check '{nameof(config.Arguments)}' or the value at " +
            $"'{nameof(config.ArgumentsPath)}' ({config.ArgumentsPath}).");
    }

    /// <summary>Disposes the transport if it is disposable (HTTP transports own their HttpClient).</summary>
    private sealed class TransportLifetime(IClientTransport transport) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            transport is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}

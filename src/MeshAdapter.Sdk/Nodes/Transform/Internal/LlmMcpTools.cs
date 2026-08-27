using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
/// MCP tool handling for LlmQuery@1: loads tools from resolved MCP servers, logs the
/// model's tool calls, and disposes the opened clients. Server resolution and transport
/// construction live in the shared <see cref="McpServerResolver"/>.
/// </summary>
internal static class LlmMcpTools
{
    /// <summary>
    /// Opens a client per server and aggregates their tools, optionally filtered by
    /// <paramref name="allowedToolNames"/> (case-insensitive; null/empty = all). A broken
    /// server logs a warning and is skipped. Opened clients are added to
    /// <paramref name="clients"/> immediately so the caller's finally block always
    /// disposes them.
    /// </summary>
    internal static async Task<IList<AIFunction>> LoadAsync(
        IList<McpServerResolver.McpServerConfig> servers,
        List<McpClient> clients,
        string[]? allowedToolNames,
        INodeContext nodeContext,
        CancellationToken ct)
    {
        var allowlist = allowedToolNames is { Length: > 0 }
            ? new HashSet<string>(allowedToolNames, StringComparer.OrdinalIgnoreCase)
            : null;

        var allTools = new List<AIFunction>();
        foreach (var server in servers)
        {
            try
            {
                IClientTransport transport = McpServerResolver.BuildTransport(server);

                var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                clients.Add(client);
                var tools = await client.ListToolsAsync(cancellationToken: ct);

                var accepted = allowlist is null
                    ? tools.ToList()
                    : tools.Where(t => allowlist.Contains(t.Name)).ToList();

                nodeContext.Debug(
                    $"MCP server '{server.Name}' contributed {accepted.Count} of {tools.Count} " +
                    $"tool(s): {string.Join(", ", accepted.Select(t => t.Name))}");

                allTools.AddRange(accepted.Cast<AIFunction>());
            }
            catch (Exception ex)
            {
                nodeContext.Warning(
                    $"Failed to connect to MCP server '{server.Name}': {ex.Message}. " +
                    "Continuing without its tools.");
            }
        }

        // Allowlist names that matched nothing are usually typos — surface them.
        if (allowlist is not null)
        {
            var unmatched = allowlist.Except(allTools.Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase).ToList();
            if (unmatched.Count > 0)
            {
                nodeContext.Warning(
                    $"McpToolNames entries matched no tool: {string.Join(", ", unmatched)}");
            }
        }

        return allTools;
    }

    /// <summary>
    /// Logs the tool invocations of a chat call. UseFunctionInvocation() runs the loop
    /// internally; the rounds are preserved in <see cref="ChatResponse.Messages"/> as
    /// FunctionCall/FunctionResult content. Arguments at Info, results truncated at Debug.
    /// </summary>
    internal static void LogToolCalls(ChatResponse response, int offeredToolCount, INodeContext nodeContext)
    {
        if (offeredToolCount == 0)
        {
            return;
        }

        var calls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        if (calls.Count == 0)
        {
            nodeContext.Info($"{offeredToolCount} MCP tool(s) offered, but the model made no tool calls");
            return;
        }

        var resultsByCallId = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .GroupBy(r => r.CallId)
            .ToDictionary(g => g.Key, g => g.First());

        nodeContext.Info($"Model made {calls.Count} MCP tool call(s) ({offeredToolCount} tool(s) offered):");
        foreach (var call in calls)
        {
            string args;
            try
            {
                args = call.Arguments is { Count: > 0 }
                    ? JsonSerializer.Serialize(call.Arguments, SystemTextJsonOptions.Default)
                    : "{}";
            }
            catch (Exception)
            {
                args = "<unserializable>";
            }

            nodeContext.Info($"  -> {call.Name}({args})");

            if (!resultsByCallId.TryGetValue(call.CallId, out var result))
            {
                continue;
            }

            string resultText;
            try
            {
                resultText = JsonSerializer.Serialize(result.Result, SystemTextJsonOptions.Default);
            }
            catch (Exception)
            {
                resultText = result.Result?.ToString() ?? "<null>";
            }

            const int maxResultLogLength = 500;
            if (resultText.Length > maxResultLogLength)
            {
                resultText = resultText[..maxResultLogLength] + $"… [{resultText.Length} chars total]";
            }

            nodeContext.Debug($"  <- {call.Name} result: {resultText}");
        }
    }

    /// <summary>
    /// Disposes every opened MCP client (stdio: shuts down the spawned subprocess;
    /// HTTP/SSE: closes the connection). Disposal failures are logged, never thrown.
    /// </summary>
    internal static async Task DisposeAsync(List<McpClient> clients, INodeContext nodeContext)
    {
        foreach (var client in clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                nodeContext.Warning($"Failed to dispose MCP client: {ex.Message}");
            }
        }

        clients.Clear();
    }
}

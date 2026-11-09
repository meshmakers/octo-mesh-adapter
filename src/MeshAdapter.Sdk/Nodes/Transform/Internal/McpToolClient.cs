using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     A failed MCP tool call (AB#8658): a JSON-RPC error, a tool error result, an unexpected HTTP status
///     or an unreadable answer. The message is meant for the pipeline execution's error and never carries
///     credentials.
/// </summary>
internal sealed class McpToolCallException(string message) : Exception(message);

/// <summary>
///     Minimal MCP client for one deterministic tool call over the Streamable HTTP transport (AB#8658):
///     <c>initialize</c> → <c>notifications/initialized</c> → <c>tools/call</c>, each with the bearer token
///     and the session id the server hands out. Answers may come as plain JSON or as an SSE stream.
/// </summary>
internal sealed class McpToolClient(HttpClient httpClient, Uri endpoint, string accessToken)
{
    /// <summary>The named HttpClient for MCP calls (long timeout; the node applies its own).</summary>
    internal const string HttpClientName = "OctoMcp";

    internal const string ProtocolVersion = "2025-03-26";

    private const int MaxQuotedBodyLength = 300;
    private const int MaxQuotedToolErrorLength = 1000;

    private string? _sessionId;

    /// <summary>
    ///     Runs the handshake and calls <paramref name="toolName" />. Returns the structured result:
    ///     <c>structuredContent</c> when the server sends it, otherwise the single text content parsed as
    ///     JSON, otherwise the text itself.
    /// </summary>
    public async Task<JsonNode?> CallToolAsync(string toolName, JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var initialize = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "OctoMesh-McpToolCallNode", ["version"] = "1.0.0" }
            }
        };
        await SendRpcAsync(initialize, 1, "initialize", cancellationToken);

        // A notification: the server answers 202 without a body; the outcome is not checked.
        using (var initialized = CreateRequest(new JsonObject
               {
                   ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized"
               }))
        {
            using var _ = await httpClient.SendAsync(initialized, cancellationToken);
        }

        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
        };
        var result = await SendRpcAsync(call, 2, $"tool '{toolName}'", cancellationToken);
        return ReadToolResult(toolName, result);
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (_sessionId != null)
        {
            request.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        return request;
    }

    private async Task<JsonObject> SendRpcAsync(JsonObject body, int id, string what,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
        {
            _sessionId = sessionIds.FirstOrDefault() ?? _sessionId;
        }

        var message = FindResponse(text, id);

        // A JSON-RPC error wins over the HTTP status: it carries the server's code and message.
        if (message?["error"] is JsonObject error)
        {
            var code = error["code"]?.ToJsonString() ?? "?";
            var errorMessage = error["message"]?.GetValue<string>() ?? "no message";
            throw new McpToolCallException($"MCP error {code} on {what}: {errorMessage}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new McpToolCallException(
                $"MCP server answered {what} with HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                (string.IsNullOrWhiteSpace(text) ? "." : $": {Truncate(text, MaxQuotedBodyLength)}"));
        }

        if (message?["result"] is not JsonObject result)
        {
            throw new McpToolCallException(
                $"MCP server sent no JSON-RPC result for {what}: {Truncate(text, MaxQuotedBodyLength)}");
        }

        return result;
    }

    /// <summary>
    ///     The JSON-RPC message answering request <paramref name="id" /> in a plain JSON body or an SSE
    ///     stream (which may carry notifications before the answer).
    /// </summary>
    internal static JsonObject? FindResponse(string body, int id)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            return TryParse(trimmed);
        }

        JsonObject? fallback = null;
        foreach (var line in body.Split('\n'))
        {
            var candidate = line.Trim();
            if (!candidate.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var message = TryParse(candidate["data:".Length..].Trim());
            if (message == null)
            {
                continue;
            }

            if (message["id"] is JsonValue value && value.TryGetValue<int>(out var messageId) && messageId == id)
            {
                return message;
            }

            if (message["result"] != null || message["error"] != null)
            {
                fallback ??= message;
            }
        }

        return fallback;
    }

    internal static JsonNode? ReadToolResult(string toolName, JsonObject result)
    {
        var texts = result["content"] is JsonArray content
            ? content.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "text")
                .Select(item => item["text"]?.GetValue<string>() ?? string.Empty)
                .ToList()
            : [];

        if (result["isError"] is JsonValue isError && isError.TryGetValue<bool>(out var failed) && failed)
        {
            throw new McpToolCallException(
                $"MCP tool '{toolName}' returned an error: {Truncate(string.Join("\n", texts), MaxQuotedToolErrorLength)}");
        }

        if (result["structuredContent"] is { } structured)
        {
            return structured.DeepClone();
        }

        if (texts.Count == 1 && TryParseAny(texts[0]) is { } parsed)
        {
            return parsed;
        }

        return texts.Count == 0 ? null : JsonValue.Create(string.Join("\n", texts));
    }

    private static JsonObject? TryParse(string json) => TryParseAny(json) as JsonObject;

    private static JsonNode? TryParseAny(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

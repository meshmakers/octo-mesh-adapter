using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The MCP side of an LLM query: the token the calls authenticate with, the session handshake, the
///     tool list and the tool calls, as JSON-RPC over HTTP against <c>{mcpServerUrl}/{tenantId}/mcp</c>.
///     Moved unchanged out of <c>AnthropicAiQueryNode</c>; the requests are the ones that node has always
///     sent. One session belongs to one node instance.
/// </summary>
/// <remarks>
///     Not <see cref="McpToolClient" />: the two differ on the wire (client name, no
///     <c>notifications/initialized</c> here, a failed handshake here only loses the tools), so this
///     class keeps the query nodes' requests as they are.
/// </remarks>
internal sealed class LlmMcpSession(
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    IServiceAccountTokenService serviceAccountTokenService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The input schema of a tool that declares none: an object without properties.</summary>
    private static readonly JsonElement EmptyInputSchema =
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

    /// <summary>
    /// MCP session ID for the current pipeline execution. Set during LoadToolsAsync (initialize handshake).
    /// </summary>
    private string? _mcpSessionId;

    /// <summary>
    /// Bearer token for the MCP server, acquired once per execution: the token of exactly the service
    /// account <see cref="LlmQueryOptions.McpServiceAccountConfigName" /> names, or the
    /// delegated token of the caller. Null when there is none; the node then does not call MCP at all
    /// (AB#8685, AB#8699).
    /// </summary>
    private string? _mcpAccessToken;

    /// <summary>
    /// Acquires the MCP token into <see cref="_mcpAccessToken" /> and returns whether MCP may be called.
    ///
    /// <para>
    /// Service-account mode: the token comes from
    /// <see cref="IServiceAccountTokenService.AcquireServiceAccountTokenAsync" />, which RETURNS the token of
    /// exactly the named account (cached per tenant, client and issuer) and never touches the process-wide
    /// <c>IServiceClientAccessToken</c> — that singleton is the adapter's own identity, shared by every
    /// pipeline of the process, so reading a token back from it could hand this node another account's token
    /// or the adapter's own (AB#8699).
    /// </para>
    /// <para>
    /// 🔴 <b>Fail-closed when the configured account yields no token (AB#8699):</b> a missing or broken
    /// <c>ServiceAccountConfiguration</c>, a refused token request or an exception all end in one warning and
    /// <c>false</c> — no MCP call, the query runs without tools. Until AB#8699 that case sent the MCP calls
    /// unauthenticated (the AB#4541 degrade); the chat still keeps working, only the unauthenticated attempt
    /// is gone.
    /// </para>
    /// <para>
    /// With <see cref="LlmQueryOptions.DelegatesToCaller" /> a <b>delegated</b> token for
    /// the calling user is acquired instead (AB#5031), and every failure throws — see
    /// <see cref="AcquireDelegatedAccessTokenAsync" />.
    /// </para>
    /// </summary>
    internal async Task<bool> EnsureAccessTokenAsync(string? serviceAccountConfigName, bool delegatesToCaller,
        INodeContext nodeContext)
    {
        // AB#5127: mcpDelegateToCaller is the AI-node alias of the general `identity` property.
        // identity: Caller (or legacy mcpDelegateToCaller: true) delegates to the calling user;
        // identity: ServiceAccount / System (the historical default, mcpDelegateToCaller absent) use
        // the service account's own token. DelegatesToCaller resolves both spellings.
        if (delegatesToCaller)
        {
            _mcpAccessToken = await AcquireDelegatedAccessTokenAsync(serviceAccountConfigName, nodeContext);
            return true;
        }

        _mcpAccessToken = null;
        if (string.IsNullOrEmpty(serviceAccountConfigName))
        {
            return false;
        }

        string? token;
        string? failure = null;
        try
        {
            token = await serviceAccountTokenService.AcquireServiceAccountTokenAsync(etlContext.TenantRepository,
                serviceAccountConfigName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AB#4541 shape (e.g. a repository error while reading the configuration): never fail the
            // whole pipeline for it — the chat keeps working without tools.
            token = null;
            failure = ex.Message;
        }

        if (string.IsNullOrEmpty(token))
        {
            nodeContext.Warning(BuildMcpServiceAccountWithoutTokenWarning(serviceAccountConfigName, failure));
            return false;
        }

        // AB#5538: the token is masked in every diagnostic output of this execution.
        nodeContext.RegisterSecret(token);
        _mcpAccessToken = token;
        nodeContext.Debug(
            $"MCP access token acquired via ServiceAccountConfiguration '{serviceAccountConfigName}'");
        return true;
    }

    /// <summary>
    ///     The warning written when the configured MCP service account yields no token, so MCP is not called
    ///     (AB#8699). Names the configuration, never a credential.
    /// </summary>
    internal static string BuildMcpServiceAccountWithoutTokenWarning(string configName, string? failure)
    {
        var reason = string.IsNullOrEmpty(failure)
            ? "see the adapter log for the reason"
            : $"acquisition failed: {failure}";
        return $"MCP ServiceAccountConfiguration '{configName}' did not yield an access token ({reason}); " +
               "no MCP call is made and the query runs without tools.";
    }

    /// <summary>
    ///     Acquires a token that runs on the <b>calling user's</b> identity — the service account of
    ///     <see cref="LlmQueryOptions.McpServiceAccountConfigName" /> acting on
    ///     behalf of the caller who triggered the pipeline (AB#5026 / AB#5031). The MCP server then
    ///     applies the user's own roles and data permissions instead of the service account's full
    ///     <c>octo_api</c> reach.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Fail-closed.</b> Every failure here throws instead of degrading. The two
    ///     degrade-to-unauthenticated paths of the service-account mode exist so a broken
    ///     ServiceAccountConfiguration still leaves a working (tool-less) chat; in delegation mode the
    ///     whole point is that the request runs with the caller's permissions, so continuing with a
    ///     service-account token — or with none at all, which an auth-enforcing MCP server answers
    ///     with zero tools but a permissive one would answer with everything — would silently defeat
    ///     the authorization this mode exists to enforce.
    /// </remarks>
    private async Task<string> AcquireDelegatedAccessTokenAsync(string? serviceAccountConfigName,
        INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(serviceAccountConfigName))
        {
            throw new InvalidOperationException(
                "mcpDelegateToCaller is set but no mcpServiceAccountConfigName is configured. Delegation " +
                "runs through a service account acting on behalf of the caller, so the " +
                "ServiceAccountConfiguration naming that account is required.");
        }

        var callerAccessToken = etlContext.CallerAccessToken;
        if (string.IsNullOrEmpty(callerAccessToken))
        {
            throw new InvalidOperationException(
                "mcpDelegateToCaller is set but the pipeline execution carries no caller token — the " +
                "trigger was anonymous or does not forward one. Use FromHttpRequest@2 with " +
                "allowAnonymous: false, or turn mcpDelegateToCaller off for a service-account-driven " +
                "pipeline. Refusing to fall back to the service account's identity.");
        }

        // The caller's own token is a credential like the token it is exchanged for: registered before the
        // exchange, so a refusal of the identity service that quotes the subject token is masked in the
        // execution log and in the error of the execution.
        nodeContext.RegisterSecret(callerAccessToken);

        var delegatedToken = await serviceAccountTokenService.AcquireDelegatedTokenAsync(
            etlContext.TenantRepository, serviceAccountConfigName, callerAccessToken);

        if (string.IsNullOrEmpty(delegatedToken))
        {
            throw new InvalidOperationException(
                $"Could not obtain a delegated access token via ServiceAccountConfiguration " +
                $"'{serviceAccountConfigName}' for the calling user (see the adapter log for the " +
                "reason reported by the identity service). Refusing to send MCP calls under another " +
                "identity or unauthenticated.");
        }

        // Registered before the first log line and before any MCP call: an MCP server or gateway that
        // echoes the bearer in an error body would otherwise put the delegated token into the log.
        nodeContext.RegisterSecret(delegatedToken);

        nodeContext.Debug(
            "MCP access token acquired by delegation (on-behalf-of the calling user) via " +
            $"ServiceAccountConfiguration '{serviceAccountConfigName}'");

        return delegatedToken;
    }

    /// <summary>
    /// Adds the <c>Authorization: Bearer</c> header to an MCP request when a service-account or
    /// delegated token has been acquired. No-op otherwise (unauthenticated local/dev calls).
    /// </summary>
    internal void AddAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_mcpAccessToken))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _mcpAccessToken);
        }
    }

    internal async Task<List<AITool>?> LoadToolsAsync(string mcpServerUrl, string tenantId, string clientName,
        INodeContext nodeContext)
    {
        try
        {
            var url = $"{mcpServerUrl}/{tenantId}/mcp";

            // Step 1: Initialize MCP session
            _mcpSessionId = await InitializeSessionAsync(url, clientName, nodeContext);

            // Step 2: List tools with session ID
            using var client = httpClientFactory.CreateClient("OctoMcp");
            var rpcRequest = new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(rpcRequest), Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            AddAuthHeader(request);
            if (_mcpSessionId != null)
            {
                request.Headers.Add("Mcp-Session-Id", _mcpSessionId);
            }

            using var response = await client.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            nodeContext.Debug($"MCP tools/list response status: {response.StatusCode}, length: {responseText.Length}, content-type: {response.Content.Headers.ContentType}");
            if (responseText.Length < 500)
            {
                nodeContext.Debug($"MCP tools/list response body: {responseText}");
            }

            // Parse SSE response — look for JSON-RPC result in the event stream
            var jsonRpcResponse = ExtractJsonRpcFromSse(responseText);
            if (jsonRpcResponse == null)
            {
                nodeContext.Warning($"Could not parse MCP tools/list response. Raw: {LogExcerpt(responseText, 200, nodeContext)}");
                return null;
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(jsonRpcResponse);
            if (!doc.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools))
            {
                return null;
            }

            // The declarations the chat client offers to the model: name, description and the input
            // schema as the MCP server delivered it.
            var declarations = new List<AITool>();
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString()!;
                var description = tool.TryGetProperty("description", out var desc)
                    ? desc.GetString() ?? ""
                    : "";

                declarations.Add(AIFunctionFactory.CreateDeclaration(name, description,
                    tool.TryGetProperty("inputSchema", out var schema) ? schema : EmptyInputSchema));
            }

            return declarations;
        }
        catch (Exception ex)
        {
            nodeContext.Warning($"Failed to load MCP tools: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> InitializeSessionAsync(string url, string clientName, INodeContext nodeContext)
    {
        using var client = httpClientFactory.CreateClient("OctoMcp");
        var initRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new
                {
                    name = clientName,
                    version = "1.0.0"
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(initRequest, JsonOptions), Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddAuthHeader(request);

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        // Extract session ID from response header
        string? sessionId = null;
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
        {
            sessionId = sessionIds.FirstOrDefault();
        }

        nodeContext.Debug($"MCP initialize: status={response.StatusCode}, sessionId={sessionId ?? "(none)"}");

        if (!response.IsSuccessStatusCode)
        {
            nodeContext.Warning($"MCP initialize failed: {LogExcerpt(responseText, 300, nodeContext)}");
        }

        return sessionId;
    }

    internal async Task<string> CallToolAsync(string? mcpServerUrl, string tenantId, string toolName,
        IDictionary<string, object?>? arguments)
    {
        using var client = httpClientFactory.CreateClient("OctoMcp");

        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments
            }
        };

        var url = $"{mcpServerUrl}/{tenantId}/mcp";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(rpcRequest, JsonOptions), Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddAuthHeader(request);
        if (_mcpSessionId != null)
        {
            request.Headers.Add("Mcp-Session-Id", _mcpSessionId);
        }

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        var jsonRpcResponse = ExtractJsonRpcFromSse(responseText);
        if (jsonRpcResponse == null)
        {
            throw new InvalidOperationException($"No valid response from MCP tool '{toolName}'");
        }

        var doc = JsonSerializer.Deserialize<JsonElement>(jsonRpcResponse);

        if (doc.TryGetProperty("error", out var error))
        {
            var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
            throw new InvalidOperationException($"MCP tool error: {errorMsg}");
        }

        if (doc.TryGetProperty("result", out var resultProp))
        {
            // MCP tool result contains "content" array with text items
            if (resultProp.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                    {
                        sb.AppendLine(text.GetString());
                    }
                }

                return sb.ToString();
            }

            return resultProp.ToString();
        }

        return responseText;
    }

    /// <summary>
    ///     The start of an MCP answer for a log line. The secrets of the execution are masked in the whole
    ///     text before it is cut: the registry masks a secret only as a whole, so a token the cut runs
    ///     through would keep its head in the log.
    /// </summary>
    private static string LogExcerpt(string text, int maxLength, INodeContext nodeContext)
    {
        var redacted = nodeContext.SecretRegistry?.Redact(text) ?? text;
        return redacted[..Math.Min(maxLength, redacted.Length)];
    }

    private static string? ExtractJsonRpcFromSse(string responseText)
    {
        // SSE format: "event: message\ndata: {json}\n\n"
        // Or direct JSON-RPC response
        if (responseText.TrimStart().StartsWith('{'))
        {
            return responseText;
        }

        foreach (var line in responseText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:"))
            {
                var jsonPart = trimmed["data:".Length..].Trim();
                if (jsonPart.StartsWith('{'))
                {
                    return jsonPart;
                }
            }
        }

        return null;
    }
}

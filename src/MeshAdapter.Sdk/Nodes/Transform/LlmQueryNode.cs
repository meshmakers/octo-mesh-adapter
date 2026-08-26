using System.ClientModel;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Newtonsoft.Json.Linq;
using OpenAI;
// IMPORTANT: do NOT add `using OpenAI.Chat;` — it conflicts with Microsoft.Extensions.AI
// on ChatMessage / ChatResponseFormat / ChatRole / ChatOptions. Use the fully-qualified
// OpenAI.Chat.ChatClient at the construction site instead.

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(LlmQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class LlmQueryNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IServiceAccountTokenService serviceAccountTokenService)
    : IPipelineNode
{
    /// <summary>
    /// Well-known ActivitySource name emitted by this node via
    /// <c>Microsoft.Extensions.AI.OpenTelemetry</c>. Use this constant when
    /// configuring OTel collectors (`AddSource(...)`) or filtering in Grafana
    /// to capture <c>gen_ai.*</c> spans produced by LlmQuery@1 calls.
    /// </summary>
    internal const string ActivitySourceName = "Meshmakers.Octo.Sdk.MeshAdapter.LlmQuery";


    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<LlmQueryNodeConfiguration>();

        // Allows for cancellation via timeout or upstream interrupt.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var ct = timeoutCts.Token;

        // MCP clients opened for this execution. They must stay alive for the
        // duration of the LLM call (the tool loop invokes them) and MUST be
        // disposed afterwards — a stdio client owns a spawned subprocess, an
        // HTTP/SSE client an open connection. Without disposal the adapter
        // leaks one subprocess/connection per processed data object. Disposal
        // happens in the finally block below, on success and failure alike.
        var mcpClients = new List<McpClient>();

        try
        {
            // Validate
            if (string.IsNullOrEmpty(config.Question))
            {
                throw new ArgumentException("Question is required", nameof(config.Question));
            }

            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(
                    nodeContext, nameof(config.Path));
            }

            // Sampling-config mutual exclusion: Temperature and TopP are
            // alternative controls for the same property of the output
            // distribution. Industry convention (OpenAI, Anthropic, Cerebras,
            // Ollama) is to set only one. Anthropic rejects requests that
            // set both; OpenAI accepts but recommends against. We treat as
            // a hard contract — same error regardless of backend.
            if (config.Temperature is not null && config.TopP is not null)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(
                    nodeContext,
                    new ArgumentException(
                        $"Sampling configuration error: Temperature ({config.Temperature}) " +
                        $"and TopP ({config.TopP}) are mutually exclusive — set one, " +
                        "leave the other null. Industry convention across OpenAI, " +
                        "Anthropic, Cerebras, and Ollama is to use only one of these " +
                        "sampling controls; Anthropic rejects requests that set both. " +
                        "For most pipelines, set only Temperature (e.g. 0.3 for " +
                        "deterministic extraction, 0.7 for varied output) and leave " +
                        "TopP null. TopK is orthogonal and may be set with either."));
            }

            // Resolve API key. May legitimately be null for self-hosted OpenAI-compat
            // backends without authentication (e.g. local Ollama). The OpenAI SDK
            // requires a non-null credential — we fall back to a dummy string.
            var apiKey = ResolveApiKey(config, etlContext, nodeContext);

            nodeContext.Debug(
                $"Starting LlmQuery (provider: {config.Provider}, model: {config.Model})");

            // Read source content. GetKind first to distinguish "path not present"
            // from "present but empty string" — both end the node, but the warning
            // text differs to help debugging. (Same pattern used by AnthropicAiQueryNode
            // after the STJ migration: nodes do not touch JToken / JsonNode directly,
            // they go through the typed path API on IDataContext.)
            string? mainContent = null;
            if (dataContext.GetKind(config.Path) is not DataKind.Undefined)
            {
                mainContent = dataContext.Get<string>(config.Path);
            }
            if (string.IsNullOrEmpty(mainContent))
            {
                nodeContext.Warning($"No content found at path: {config.Path}");
                await next(dataContext, nodeContext);
                return;
            }

            // Build the context (main content + any additional data paths)
            var context = BuildContext(mainContent, config.DataPaths, dataContext, nodeContext);

            // JSON mode: normalize straight double quotes in the CONTEXT to typographic
            // ones (" -> ”).
            if (config.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                context = context.Replace('"', '”');
            }

            // Build the user prompt (CONTENT / QUESTION / optional JSON sample)
            var userPrompt = BuildUserPrompt(config.Question, context,
                config.ResponseFormat, config.JsonFormatSample);

            // Construct IChatClient based on provider
            var client = ConstructClient(config, apiKey);

            // Load MCP servers + tools. Empty list when McpConfigurationNames is
            // empty, in which case the LLM runs in plain chat mode with no tool
            // plumbing. One broken MCP server logs a warning and is skipped —
            // remaining servers still contribute their tools. Loading happens
            // before message construction so a connection failure surfaces in
            // logs before we send a useless prompt.
            var mcpServers = McpServerResolver.Resolve(config.McpConfigurationNames, etlContext, nodeContext);

            // Acquire client-credentials bearers for servers that reference a
            // ServiceAccountConfiguration (AB#4315: octo-mcp-service requires a
            // bearer on every MCP request). Cached per configuration name in the
            // singleton provider — typically one identity-server round-trip per
            // token lifetime, not per pipeline message.
            if (mcpServers.Count > 0)
            {
                mcpServers = await McpServerResolver.ApplyServiceAccountTokensAsync(
                    mcpServers, serviceAccountTokenService, etlContext, nodeContext, ct);
            }

            var mcpTools = mcpServers.Count > 0
                ? await LoadMcpToolsAsync(mcpServers, mcpClients, nodeContext, ct)
                : (IList<AIFunction>)Array.Empty<AIFunction>();

            // Build messages list: System, optional history, then current user prompt.
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, config.SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            if (!string.IsNullOrEmpty(config.ConversationHistoryPath))
            {
                var history = LoadHistory(config.ConversationHistoryPath, dataContext, nodeContext);
                if (history.Count > 0)
                {
                    // Insert history between System (index 0) and User (index 1).
                    messages.InsertRange(1, history);
                }
            }

            // Build ChatOptions from config. Tools is null when no MCP is
            // configured (LLM runs in plain chat mode); otherwise it carries
            // the aggregated AIFunction list from every connected MCP server.
            // MEAI's UseFunctionInvocation() middleware (added in both client
            // factories) routes tool_use blocks back to the matching
            // McpClientTool — provider-agnostic, works for Anthropic-native
            // tool_use and OpenAI function_call equally.
            // ResponseFormat handling — only send response_format when JSON is
            // explicitly requested AND no tools are attached. Two reasons:
            //   1. Text is every provider's default, so sending response_format=text
            //      is redundant — and Cerebras (and other strict OpenAI-compatible
            //      backends) reject any request carrying both "tools" and
            //      "response_format" ("\"tools\" is incompatible with
            //      \"response_format\"", HTTP 400). Omitting it for the text case
            //      keeps tool-calling working everywhere.
            //   2. JSON-mode + tools is mutually exclusive on the same providers.
            //      When both are configured we drop response_format and rely on the
            //      system prompt to enforce JSON, logging a warning so the operator
            //      knows structured-output enforcement was relaxed for this call.
            var wantsJson = config.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase);
            var hasTools = mcpTools.Count > 0;

            if (wantsJson && hasTools)
            {
                nodeContext.Warning(
                    "responseFormat=json is incompatible with MCP tools on most providers; " +
                    "sending the request without response_format and relying on the system " +
                    "prompt to enforce JSON. Make sure the system prompt requests JSON output." +
                    (string.IsNullOrWhiteSpace(config.JsonSchema)
                        ? string.Empty
                        : " The configured jsonSchema is NOT enforced in tool mode."));
            }

            // Server-side structured output (AB#4464 follow-up): when a jsonSchema is
            // configured, the schema is compiled into a grammar by the provider
            // (Anthropic structured outputs / OpenAI json_schema) and constrains token
            // generation itself — guaranteed-parseable JSON at zero extra token cost.
            // Preferred over prompt-enforced JSON, which demonstrably fails on
            // quote-heavy content (unescaped quotes in verbatim citations).
            JsonElement? responseSchema = null;
            if (wantsJson && !hasTools && !string.IsNullOrWhiteSpace(config.JsonSchema))
            {
                try
                {
                    responseSchema = JsonSerializer.Deserialize<JsonElement>(config.JsonSchema);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"jsonSchema is not valid JSON: {ex.Message}");
                }
            }
            else if (wantsJson && !hasTools && config.Provider == LlmProvider.Anthropic)
            {
                // Anthropic's API has no plain JSON mode; without a jsonSchema the JSON
                // discipline is prompt-only and may fail on quote-heavy content.
                nodeContext.Warning(
                    "Anthropic without jsonSchema: JSON is prompt-enforced only. " +
                    "Set jsonSchema for guaranteed structured output.");
            }

            var options = new ChatOptions
            {
                ModelId = config.Model,
                MaxOutputTokens = config.MaxTokens,
                Temperature = (float?)config.Temperature,
                TopP = config.TopP,
                TopK = config.TopK,
                ResponseFormat = wantsJson && !hasTools
                    ? responseSchema is not null
                        ? ChatResponseFormat.ForJsonSchema(responseSchema.Value,
                            schemaName: "llm_query_response")
                        : ChatResponseFormat.Json
                    : null,
                Tools = hasTools ? [..mcpTools] : null
            };

            nodeContext.Debug($"Calling LLM with {context.Length} characters of context");

            // Call the LLM via the IChatClient abstraction
            var response = await client.GetResponseAsync(messages, options, ct);
            var aiResponse = response.Text;

            LogToolCalls(response, mcpTools.Count, nodeContext);

            if (string.IsNullOrEmpty(aiResponse))
            {
                throw new InvalidOperationException("Empty response from LLM");
            }

            nodeContext.Info(
                $"Received LLM response with {aiResponse.Length} characters " +
                $"({response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount} tokens)");

            // Parse + post-process the response (mirrors the Anthropic node's pattern,
            // including the prose-wrapped-JSON extraction fallback in ExtractJsonFromText).
            // For JSON responses that fail to parse, a bounded repair pass re-asks the
            // model with ONLY the broken output + parser error (not the original context).
            var processedResponse = await ProcessJsonResponseWithRepairAsync(
                aiResponse, config, client, options, nodeContext, ct);

            // Store the processed response via the typed STJ-based Set API.
            // The serializer argument from the old API is gone — IDataContext is
            // STJ-native and uses SystemTextJsonOptions.Default internally.
            dataContext.Set(
                config.TargetPath,
                processedResponse,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode
            );

            // Optionally also store the raw response
            if (config.IncludeRawResponse)
            {
                dataContext.Set(
                    config.RawResponseOutputPath ?? "$.RawAiResponse",
                    aiResponse,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode
                );
            }

            nodeContext.Info("LlmQuery completed successfully");
        }
        catch (OperationCanceledException oce) when (timeoutCts.IsCancellationRequested)
        {
            // Our own TimeoutSeconds budget elapsed (not an upstream interrupt).
            // The raw exception ("A task was canceled.") hides this — surface a
            // clear, actionable message. Common causes: the provider call plus
            // the MCP tool-call loop exceeded TimeoutSeconds; a slow remote MCP
            // server (DeepWiki ask_question can take tens of seconds) across
            // several MaxToolRounds; or the provider repeatedly returning a
            // retryable status (429/5xx) so the SDK backs off until the budget
            // runs out.
            nodeContext.Error(
                $"LlmQuery timed out after {config.TimeoutSeconds}s (provider call + tool loop " +
                $"exceeded the budget). Increase TimeoutSeconds, lower MaxToolRounds, or check " +
                $"MCP server latency / provider rate limits.");
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, oce);
        }
        catch (OperationCanceledException)
        {
            // Genuine upstream cancellation (pipeline shutdown / caller abort).
            // ContinueOnError does not apply to deliberate cancellation — let the
            // pipeline runtime see it and react.
            throw;
        }
        catch (ClientResultException cre)
        {
            // Provider returned a non-success HTTP status (e.g. 400 from Cerebras
            // rejecting an unsupported tool schema, 401 bad key, 429 rate limit).
            // The default exception message is only "Service request failed.
            // Status: 400 (Bad Request)" — the actionable detail lives in the raw
            // response body. Surface it so model/MCP-schema incompatibilities are
            // diagnosable from the pipeline log instead of opaque 400s.
            var body = cre.GetRawResponse()?.Content?.ToString();
            nodeContext.Error(
                $"LLM provider request failed (HTTP {cre.Status}): {body ?? cre.Message}");

            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, cre);
            }
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during LlmQuery ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            await DisposeMcpClientsAsync(mcpClients, nodeContext);
        }

        await next(dataContext, nodeContext);
    }

    // ----------------------------------------------------------------------
    // Provider factory — Spike 2 added OpenAI-compatible; Spike 4 added the
    // Anthropic-native branch. Both produce an IChatClient with identical
    // contract surface (response, streaming, JSON, cancellation, OTel) so
    // the rest of ProcessObjectAsync is provider-agnostic. The difference
    // is in the gen_ai.provider.name attribute on emitted spans:
    //   OpenAiCompatible → "openai" (regardless of actual backend)
    //   Anthropic        → "anthropic"
    // server.address and openai.response.system_fingerprint pin the real
    // backend within each branch.
    // ----------------------------------------------------------------------

    private static IChatClient ConstructClient(LlmQueryNodeConfiguration config, string? apiKey)
    {
        return config.Provider switch
        {
            LlmProvider.OpenAiCompatible => ConstructOpenAiCompatibleClient(config, apiKey),
            LlmProvider.Anthropic        => ConstructAnthropicClient(config, apiKey),
            _ => throw new ArgumentOutOfRangeException(nameof(config.Provider),
                $"Unsupported provider: {config.Provider}")
        };
    }

    private static IChatClient ConstructOpenAiCompatibleClient(
        LlmQueryNodeConfiguration config, string? apiKey)
    {
        // ApiKey may be a real API key (OpenAI cloud, Azure OpenAI, OpenRouter, vLLM
        // with auth) or a dummy string (Ollama, self-hosted without auth — must be
        // non-empty per the OpenAI SDK contract).
        var credential = new ApiKeyCredential(apiKey ?? "unused");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        // Fully-qualified ChatClient avoids the OpenAI.Chat / Microsoft.Extensions.AI
        // namespace ambiguity on ChatMessage / ChatOptions / etc.
        //
        // The .UseOpenTelemetry() decorator emits gen_ai.* spans on the
        // ActivitySourceName above — provider-agnostic telemetry that lets the
        // same Grafana panels work across OpenAI, Anthropic, Ollama, etc.
        // Sensitive data (prompts/responses) is OFF by default; flip via
        // configure callback if a future audit needs it.
        // .UseFunctionInvocation() enables MEAI's tool-call loop: when the LLM
        // returns tool_use blocks, the middleware invokes the matching AIFunction
        // from ChatOptions.Tools and feeds results back automatically. Required
        // for MCP integration — McpClientTool extends AIFunction, so MCP tools
        // become callable through this same path. The middleware is harmless
        // when ChatOptions.Tools is empty (no MCP configured).
        // MaximumIterationsPerRequest enforces the configured MaxToolRounds
        // budget — without it, MEAI's own default applies and the node-level
        // knob would be dead config.
        return new OpenAI.Chat.ChatClient(
                model: config.Model,
                credential: credential,
                options: options)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: ActivitySourceName)
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = config.MaxToolRounds)
            .Build();
    }

    private static IChatClient ConstructAnthropicClient(
        LlmQueryNodeConfiguration config, string? apiKey)
    {
        // Anthropic requires a real API key — unlike Ollama's auth-less local
        // mode, there is no public anonymous endpoint. Fail fast with a clear
        // message rather than letting the SDK throw a confusing 401.
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                "Anthropic provider requires an API key. Set ApiKeyConfigurationName " +
                "to reference an AiConfiguration entity that holds it.");
        }

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };

        // BaseUrl override is for self-hosted / proxy scenarios (Workspaces,
        // enterprise gateways). When null, the SDK uses the default
        // https://api.anthropic.com endpoint.
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            client = (Anthropic.AnthropicClient)client
                .WithOptions(o => o with { BaseUrl = config.BaseUrl });
        }

        // .AsIChatClient(model) — the official Anthropic SDK's IChatClient
        // bridge. Model passed here becomes the default; ChatOptions.ModelId
        // can override it per-call but our node body always sets it
        // explicitly, so the default is the only path that fires.
        return client
            .AsIChatClient(config.Model)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: ActivitySourceName)
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = config.MaxToolRounds)
            .Build();
    }

    private static string? ResolveApiKey(
        LlmQueryNodeConfiguration config, IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        // API keys are only ever read from an AiConfiguration entity — no inline key on
        // the node, so keys cannot end up in pipeline definitions, seeds or exports.
        // Null is legitimate for backends without authentication (e.g. local Ollama).
        if (string.IsNullOrEmpty(config.ApiKeyConfigurationName)) return null;

        if (etlContext.GlobalConfiguration.IsDefined(config.ApiKeyConfigurationName))
        {
            var rawJson = etlContext.GlobalConfiguration.GetRawJson(config.ApiKeyConfigurationName);
            var configDoc = JObject.Parse(rawJson);
            var key = configDoc.Value<string>("apiKey");
            if (!string.IsNullOrEmpty(key))
            {
                nodeContext.Debug(
                    $"API key loaded from configuration '{config.ApiKeyConfigurationName}'");
                return key;
            }
        }

        nodeContext.Warning(
            $"AiConfiguration '{config.ApiKeyConfigurationName}' not found or has no ApiKey; " +
            "continuing without an API key (only valid for backends without authentication).");

        return null;
    }

    // ----------------------------------------------------------------------
    // Context construction: assembles main content + optional additional
    // data-path values into a single string for the user prompt.
    // ----------------------------------------------------------------------

    private static string BuildContext(
        string mainContent, string[]? dataPaths, IDataContext dataContext, INodeContext nodeContext)
    {
        var ctx = new StringBuilder();
        ctx.AppendLine("Main Content:");
        ctx.AppendLine(mainContent);
        ctx.AppendLine();

        if (dataPaths is not { Length: > 0 }) return ctx.ToString();
        ctx.AppendLine("Additional Context:");
        foreach (var dataPath in dataPaths)
        {
            try
            {
                // Mirror AnthropicAiQueryNode's STJ-era pattern: probe with GetKind,
                // then read scalars via Get<string> and structured values via Get<object?>
                // (which materializes to a JsonElement we can re-serialize indented).
                var kind = dataContext.GetKind(dataPath);
                if (kind is DataKind.Undefined) continue;

                ctx.AppendLine($"Data from {dataPath}:");
                if (kind == DataKind.String)
                {
                    ctx.AppendLine(dataContext.Get<string>(dataPath));
                }
                else
                {
                    var value = dataContext.Get<object?>(dataPath);
                    ctx.AppendLine(JsonSerializer.Serialize(value,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        }));
                }

                ctx.AppendLine();
            }
            catch (Exception ex)
            {
                nodeContext.Warning(
                    $"Could not retrieve data from path {dataPath}: {ex.Message}");
            }
        }

        return ctx.ToString();
    }

    // ----------------------------------------------------------------------
    // Conversation history loader: reads role/content pairs from a JSON
    // array at the configured path and converts them to ChatMessages.
    // ----------------------------------------------------------------------

    private static List<ChatMessage> LoadHistory(
        string historyPath, IDataContext dataContext, INodeContext nodeContext)
    {
        var result = new List<ChatMessage>();

        // STJ-era equivalent of "is this path a JSON array?" — GetKind reports the
        // structural kind without materializing the value. If not an array, bail.
        if (dataContext.GetKind(historyPath) != DataKind.Array)
        {
            return result;
        }

        // SelectMatches returns one detached sub-context per JSONPath match.
        // We use [*] to enumerate the array elements; each sub-context's $ is
        // the element itself, so we read role and content via Get<string> from $.
        foreach (var entry in dataContext.SelectMatches($"{historyPath}[*]"))
        {
            var role = entry.GetKind("$.role") is DataKind.String
                ? entry.Get<string>("$.role")
                : null;
            var content = entry.GetKind("$.content") switch
            {
                DataKind.String => entry.Get<string>("$.content"),
                DataKind.Array => ExtractTextFromContentParts(entry),
                _ => null
            };
            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(content))
            {
                continue;
            }

            var chatRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            result.Add(new ChatMessage(chatRole, content));
        }

        nodeContext.Debug($"Loaded {result.Count} messages from conversation history");
        return result;
    }

    /// <summary>
    /// Flattens an OpenAI content-parts array ("content": [{"type":"text","text":"..."}])
    /// into a single string by concatenating the text parts in order. Non-text parts
    /// (image_url, input_audio, ...) are ignored. Returns null when no text part is
    /// present, so the caller's empty-content skip applies unchanged.
    /// </summary>
    private static string? ExtractTextFromContentParts(IDataContext entry)
    {
        var sb = new StringBuilder();
        foreach (var part in entry.SelectMatches("$.content[*]"))
        {
            if (part.GetKind("$.type") is not DataKind.String ||
                !string.Equals(part.Get<string>("$.type"), "text", StringComparison.OrdinalIgnoreCase) ||
                part.GetKind("$.text") is not DataKind.String)
            {
                continue;
            }

            var text = part.Get<string>("$.text");
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    // ----------------------------------------------------------------------
    // Helpers kept verbatim from AnthropicAiQueryNode for response handling.
    // Provider-agnostic, battle-tested for JSON extraction from mixed-prose
    // responses (relevant for Ollama and other less-strict backends).
    // Do not remove without replacing the equivalent functionality.
    // ----------------------------------------------------------------------

    private static string BuildUserPrompt(string question, string context, string responseFormat,
        string jsonExample)
    {
        var promptBuilder = new StringBuilder();

        promptBuilder.AppendLine("Please analyze the following content and answer the question provided.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("CONTENT:");
        promptBuilder.AppendLine("---");
        promptBuilder.AppendLine(context);
        promptBuilder.AppendLine("---");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("QUESTION:");
        promptBuilder.AppendLine(question);
        promptBuilder.AppendLine();

        if (!responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase)) return promptBuilder.ToString();

        if (string.IsNullOrWhiteSpace(jsonExample))
        {
            // No example configured: request JSON without inventing a domain-specific
            // sample (a default example would leak unrelated fields into every prompt).
            promptBuilder.AppendLine("Please provide your response in valid JSON format.");
        }
        else
        {
            promptBuilder.AppendLine("Please provide your response in valid JSON format. For example:");
            promptBuilder.AppendLine(jsonExample);
        }

        return promptBuilder.ToString();
    }

    /// <summary>
    /// Parses a JSON response, with the prose-wrapped-JSON extraction fallback. On failure,
    /// runs a BOUNDED repair loop (hard-capped at 2 attempts): the repair request contains
    /// ONLY the broken output and the parser error — not the original context — so its cost
    /// is proportional to the output size and there is no retry-until-timeout by
    /// construction. Returns the parsed JsonElement, or the original text when repair is
    /// disabled/exhausted (pre-existing fallback behavior, fails loudly downstream).
    /// Internal for unit tests (InternalsVisibleTo).
    /// </summary>
    internal static async Task<object> ProcessJsonResponseWithRepairAsync(
        string aiResponse, LlmQueryNodeConfiguration config, IChatClient client,
        ChatOptions options, INodeContext nodeContext, CancellationToken ct)
    {
        if (!config.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return aiResponse;
        }

        if (TryParseJson(aiResponse, nodeContext, out var element, out var parseError))
        {
            return element;
        }

        var maxAttempts = Math.Clamp(config.MaxJsonRepairAttempts, 0, 2);
        var current = aiResponse;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            nodeContext.Warning(
                $"Response is not valid JSON ({parseError}); bounded repair attempt " +
                $"{attempt}/{maxAttempts} (sending only the broken output back, not the original context)");
            nodeContext.Debug($"Unparseable response excerpt: {Truncate(current, 500)}");

            var repairMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You repair invalid JSON. Return ONLY the corrected JSON document - " +
                    "no prose, no code fences. Preserve the content exactly; fix only the " +
                    "syntax (escaping, delimiters, brackets)."),
                new(ChatRole.User,
                    $"The following JSON is invalid. Parser error: {parseError}\n\n{current}")
            };

            var repairOptions = new ChatOptions
            {
                ModelId = options.ModelId,
                MaxOutputTokens = options.MaxOutputTokens,
                Temperature = 0f,
                ResponseFormat = options.ResponseFormat ?? ChatResponseFormat.Json
            };

            var repairResponse = await client.GetResponseAsync(repairMessages, repairOptions, ct);
            current = repairResponse.Text ?? string.Empty;

            if (TryParseJson(current, nodeContext, out element, out parseError))
            {
                nodeContext.Info($"JSON repaired successfully on attempt {attempt}");
                return element;
            }
        }

        nodeContext.Warning(maxAttempts > 0
            ? $"JSON could not be repaired after {maxAttempts} attempt(s) ({parseError}). Returning as text."
            : $"Could not parse JSON and repair is disabled ({parseError}). Returning as text.");
        return aiResponse;
    }

    /// <summary>
    /// Whole-string parse first; on failure, the prose-wrapped-JSON extraction fallback
    /// (```json fences, then brace scanning). STJ deliberately — see the serializer note
    /// in the git history of ProcessResponse.
    /// </summary>
    private static bool TryParseJson(string text, INodeContext nodeContext,
        out JsonElement element, out string? error)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(text);
            error = null;
            return true;
        }
        catch (JsonException outerEx)
        {
            var extractedJson = ExtractJsonFromText(text);
            var candidate = extractedJson ?? text;
            if (extractedJson != null)
            {
                try
                {
                    element = JsonSerializer.Deserialize<JsonElement>(extractedJson);
                    nodeContext.Debug("Successfully extracted JSON from mixed response");
                    error = null;
                    return true;
                }
                catch (JsonException innerEx)
                {
                    outerEx = innerEx;
                }
            }

            // Deterministic repair tier: mechanical defects (naked quotes inside strings,
            // trailing commas, unclosed strings/brackets) have mechanical fixes — free and
            // instant, before any LLM repair call is considered.
            var repaired = RepairJsonMechanically(candidate);
            if (repaired != candidate)
            {
                try
                {
                    element = JsonSerializer.Deserialize<JsonElement>(repaired);
                    nodeContext.Info("JSON repaired deterministically (no LLM call)");
                    error = null;
                    return true;
                }
                catch (JsonException)
                {
                    // fall through to the original error
                }
            }

            element = default;
            error = outerEx.Message;
            return false;
        }
    }

    /// <summary>
    /// Mechanical JSON repair for the defect classes LLMs actually produce:
    /// (1) naked double quotes inside string values — a quote whose next non-whitespace
    /// character is not valid JSON continuation (, : } ] or end) is content, not a
    /// terminator, and gets escaped; (2) trailing commas before } or ]; (3) unterminated
    /// strings and unclosed braces/brackets at the end of the document. Purely syntactic —
    /// content judgment stays with the LLM repair tier. A wrong guess on (1) produces a
    /// mis-spliced string, which downstream verification (quote-verbatim checks) flags.
    /// </summary>
    internal static string RepairJsonMechanically(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 16);
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '\\':
                        escaped = true;
                        sb.Append(c);
                        continue;
                    case '"':
                    {
                        // Terminator or content? Look at the next non-whitespace char.
                        var j = i + 1;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        var isTerminator = j >= text.Length ||
                                           text[j] is ',' or ':' or '}' or ']';
                        if (isTerminator)
                        {
                            inString = false;
                            sb.Append(c);
                        }
                        else
                        {
                            sb.Append("\\\""); // content — escape it
                        }

                        continue;
                    }
                    default:
                        sb.Append(c);
                        continue;
                }
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    sb.Append(c);
                    break;
                case '{':
                    stack.Push('}');
                    sb.Append(c);
                    break;
                case '[':
                    stack.Push(']');
                    sb.Append(c);
                    break;
                case '}' or ']':
                    if (stack.Count > 0 && stack.Peek() == c) stack.Pop();
                    // Trailing comma before a closer: drop the comma.
                    TrimTrailingComma(sb);
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Close what the model left open (truncated output).
        if (inString) sb.Append('"');
        while (stack.Count > 0)
        {
            TrimTrailingComma(sb);
            sb.Append(stack.Pop());
        }

        return sb.ToString();

        static void TrimTrailingComma(System.Text.StringBuilder builder)
        {
            var k = builder.Length - 1;
            while (k >= 0 && char.IsWhiteSpace(builder[k])) k--;
            if (k >= 0 && builder[k] == ',') builder.Remove(k, 1);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string? ExtractJsonFromText(string text)
    {
        var jsonBlockStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonBlockStart >= 0)
        {
            var jsonStart = text.IndexOf('\n', jsonBlockStart) + 1;
            var jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                return text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        var braceStart = text.IndexOf('{');
        if (braceStart < 0) return null;

        var braceCount = 0;
        for (var i = braceStart; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    braceCount++;
                    break;
                case '}':
                    braceCount--;
                    break;
            }

            if (braceCount != 0) continue;
            var jsonCandidate = text.Substring(braceStart, i - braceStart + 1);
            if (jsonCandidate.Contains('"') && jsonCandidate.Contains(':'))
            {
                return jsonCandidate;
            }
        }

        return null;
    }

    // =========================================================================
    // MCP — Model Context Protocol tool loading
    // =========================================================================
    // LlmQuery@1 wires zero or more MCP servers into the LLM call as tool
    // providers. At pipeline-execution time we:
    //   1. Resolve each named System.Communication/McpConfiguration entity
    //      from GlobalConfiguration (mirrors ResolveApiKey's idiom)
    //   2. Open an McpClient per server (stdio subprocess, SSE, or HTTP)
    //   3. Aggregate ListToolsAsync() results into one IList<AIFunction>
    //   4. Pass to the LLM via ChatOptions.Tools — MEAI's UseFunctionInvocation
    //      middleware (added in both client factories) routes tool_use blocks
    //      back to the matching McpClientTool, provider-agnostic
    //
    // A broken MCP server logs a warning and is skipped — remaining servers
    // still contribute their tools. Empty McpConfigurationNames = no MCP,
    // LLM runs in plain chat mode.
    //
    // MCP server resolution, transport construction (Bearer / AdditionalHeaders
    // auth) and the McpTransport / McpServerConfig types live in the shared
    // McpServerResolver, reused by McpToolCall@1. LlmQuery lists the resolved
    // servers' tools and hands them to the model (below); McpToolCall invokes a
    // single tool directly without an LLM.

    /// <summary>
    /// Logs the MCP tool invocations of a chat call for reproducibility and
    /// troubleshooting. <c>UseFunctionInvocation()</c> runs the tool-call loop
    /// internally and logs nothing to the pipeline log; the intermediate rounds
    /// are preserved in <see cref="ChatResponse.Messages"/> as
    /// <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/>
    /// entries, which is where this method picks them up. Call arguments are
    /// logged at Info (small, high diagnostic value); tool results at Debug,
    /// truncated, since they can be large.
    /// </summary>
    private static void LogToolCalls(ChatResponse response, int offeredToolCount, INodeContext nodeContext)
    {
        if (offeredToolCount == 0)
        {
            return; // No MCP tools were offered — nothing to report.
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

    private static async Task<IList<AIFunction>> LoadMcpToolsAsync(
        IList<McpServerResolver.McpServerConfig> servers,
        List<McpClient> clients,
        INodeContext nodeContext,
        CancellationToken ct)
    {
        var allTools = new List<AIFunction>();
        foreach (var server in servers)
        {
            try
            {
                IClientTransport transport = McpServerResolver.BuildTransport(server);

                var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                // Register for disposal immediately — if ListToolsAsync throws,
                // the caller's finally still cleans up this client.
                clients.Add(client);
                var tools = await client.ListToolsAsync(cancellationToken: ct);

                nodeContext.Debug(
                    $"MCP server '{server.Name}' contributed {tools.Count} tool(s): " +
                    string.Join(", ", tools.Select(t => t.Name)));

                allTools.AddRange(tools.Cast<AIFunction>());
            }
            catch (Exception ex)
            {
                nodeContext.Warning(
                    $"Failed to connect to MCP server '{server.Name}': {ex.Message}. " +
                    "Continuing without its tools.");
            }
        }
        return allTools;
    }

    /// <summary>
    /// Disposes every MCP client opened for this execution. Stdio clients shut
    /// down their spawned subprocess (SDK default ShutdownTimeout 5s); HTTP/SSE
    /// clients close their connection. Disposal failures are logged and do not
    /// mask the original pipeline outcome.
    /// </summary>
    private static async Task DisposeMcpClientsAsync(List<McpClient> clients, INodeContext nodeContext)
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

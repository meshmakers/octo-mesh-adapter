using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The Anthropic Messages API as a chat client of Microsoft.Extensions.AI: one <c>POST /v1/messages</c>
///     per call, no tool loop and no retries (the <see cref="LlmQueryEngine" /> owns both). The request
///     body, the prompt-cache breakpoints and the error texts are the code <c>AnthropicAiQueryNode</c> has
///     always sent, moved here unchanged; this class only translates between that body and the chat
///     types. There is no Anthropic SDK underneath.
/// </summary>
/// <remarks>
///     What a caller needs to know about the translation:
///     <list type="bullet">
///         <item>An assistant message this client returned is sent back as the API delivered it
///         (<see cref="ChatMessage.RawRepresentation" />), so a tool round replays every block unchanged.</item>
///         <item>A <see cref="ChatRole.Tool" /> message of <see cref="FunctionResultContent" /> becomes the
///         <c>user</c> message with <c>tool_result</c> blocks; a result with an
///         <see cref="FunctionResultContent.Exception" /> is marked <c>is_error</c>.</item>
///         <item>A text part marked with <see cref="LlmQueryEngine.CacheBreakpointProperty" /> carries a cache
///         breakpoint.</item>
///         <item>Model and output-token limit are required. Without a temperature the body carries no
///         <c>temperature</c> key and the model's default applies. The temperature of the chat
///         types is a <see cref="float" />; the exact value is read from
///         <see cref="LlmQueryEngine.ExactTemperatureProperty" /> when the caller supplies it, otherwise the
///         float is sent as its shortest decimal text, so <c>0.1</c> stays <c>0.1</c>.</item>
///         <item>A refusal of the temperature (HTTP 400, <c>invalid_request_error</c> whose message names
///         the temperature) is thrown as <see cref="LlmSamplingRejectedException" />; every other failure as
///         <see cref="HttpRequestException" />. A redirect is not followed (the adapter's provider client
///         allows none) and fails like any other status.</item>
///         <item>The endpoint is <c>{baseUrl}/v1/messages</c>; without a base URL the public API.</item>
///         <item>Usage follows the convention of the chat types: <see cref="UsageDetails.InputTokenCount" />
///         is all input (the API's <c>input_tokens</c> plus cache reads and cache writes),
///         <see cref="UsageDetails.CachedInputTokenCount" /> the cache reads; the cache writes are in <see cref="UsageDetails.AdditionalCounts" /> under
///         <see cref="AiUsageAccumulator.CacheCreationInputTokensCount" />.</item>
///     </list>
/// </remarks>
internal sealed class AnthropicMessagesChatClient(
    IHttpClientFactory httpClientFactory,
    string apiKey,
    Uri? baseUrl = null) : IChatClient
{
    /// <summary>Name of the HTTP client the adapter registers for the Messages API.</summary>
    internal const string HttpClientName = "Anthropic";

    private const string DefaultMessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private HttpClient? _httpClient;

    /// <summary>The URL every request of this client is posted to.</summary>
    internal string MessagesEndpoint { get; } = BuildMessagesEndpoint(baseUrl);

    /// <summary><c>{baseUrl}/v1/messages</c> (a trailing slash of the base is dropped), or the public API.</summary>
    internal static string BuildMessagesEndpoint(Uri? baseUrl)
    {
        return baseUrl == null ? DefaultMessagesEndpoint : baseUrl.AbsoluteUri.TrimEnd('/') + "/v1/messages";
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _httpClient ??= httpClientFactory.CreateClient(HttpClientName);

        var model = options?.ModelId ??
                    throw new ArgumentException("The Messages API needs a model.", nameof(options));
        var maxTokens = options.MaxOutputTokens ??
                        throw new ArgumentException("The Messages API needs an output-token limit.", nameof(options));
        double? temperature = options.Temperature is { } chatTemperature ? ToWireTemperature(chatTemperature) : null;
        if (temperature != null && options.AdditionalProperties != null &&
            options.AdditionalProperties.TryGetValue(LlmQueryEngine.ExactTemperatureProperty, out var exact) &&
            exact is double exactTemperature)
        {
            temperature = exactTemperature;
        }

        // Build request — system prompt and tool list carry prompt-cache breakpoints.
        var requestObj = BuildRequestBody(model, maxTokens, temperature, options.Instructions,
            messages.Select(ToWireMessage).ToList(), ToWireTools(options.Tools), ToOutputSchema(options.ResponseFormat));

        var jsonRequest = JsonSerializer.Serialize(requestObj, JsonOptions);

        var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
        {
            Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Classify the failure so the pipeline execution's ErrorMessage says *why* the AI
            // query could not run (rate limit / token quota, overloaded, or input over the
            // context window) instead of a bare status code. The raw body is kept for
            // diagnostics. This message is what lands in the RtPipelineExecution audit record.
            var message = BuildAnthropicApiErrorMessage(response.StatusCode, responseContent);
            if (temperature != null && IsSamplingRejection(response.StatusCode, responseContent))
            {
                throw new LlmSamplingRejectedException(message);
            }

            throw new HttpRequestException(message);
        }

        return ToChatResponse(JsonSerializer.Deserialize<JsonElement>(responseContent), model);
    }

    /// <summary>Not supported: the engine reads whole answers.</summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The Anthropic Messages chat client does not stream.");
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>
    ///     The temperature as the node configured it: the shortest decimal text of the float, read back
    ///     as a double. A plain widening would send <c>0.1</c> as <c>0.10000000149011612</c>.
    /// </summary>
    internal static double ToWireTemperature(float temperature)
    {
        return double.Parse(temperature.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>One chat message as the Messages API expects it (see the remarks of the class).</summary>
    private static object ToWireMessage(ChatMessage message)
    {
        if (message.RawRepresentation is JsonElement rawContent)
        {
            return new { role = message.Role.Value, content = rawContent };
        }

        // The role alone does not make a tool-result message: conversation history passes roles on
        // as written, and an entry that calls itself "tool" is still text.
        if (message.Role == ChatRole.Tool && message.Contents.All(c => c is FunctionResultContent))
        {
            var toolResults = new List<object>();
            foreach (var result in message.Contents.Cast<FunctionResultContent>())
            {
                var text = result.Result as string ?? result.Result?.ToString();
                toolResults.Add(result.Exception == null
                    ? new { type = "tool_result", tool_use_id = result.CallId, content = text }
                    : new { type = "tool_result", tool_use_id = result.CallId, is_error = true, content = text });
            }

            return new { role = "user", content = toolResults };
        }

        var parts = message.Contents.OfType<TextContent>().ToList();
        object textContent = parts.Count == 1 && !HasCacheBreakpoint(parts[0])
            ? parts[0].Text
            : parts.Select(part => (object)BuildTextBlock(part.Text, HasCacheBreakpoint(part))).ToArray();
        return new { role = message.Role.Value, content = textContent };
    }

    private static bool HasCacheBreakpoint(AIContent content)
    {
        return content.AdditionalProperties != null &&
               content.AdditionalProperties.TryGetValue(LlmQueryEngine.CacheBreakpointProperty, out var value) &&
               value is true;
    }

    private static Dictionary<string, object> BuildTextBlock(string text, bool cacheBreakpoint)
    {
        var block = new Dictionary<string, object> { ["type"] = "text", ["text"] = text };
        if (cacheBreakpoint)
        {
            block["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" };
        }

        return block;
    }

    /// <summary>
    ///     The tool declarations as Anthropic tools (<c>name</c>, <c>description</c>, <c>input_schema</c>);
    ///     the schema is passed on as the tool server delivered it.
    /// </summary>
    private static List<JsonElement>? ToWireTools(IList<AITool>? tools)
    {
        if (tools == null)
        {
            return null;
        }

        var wireTools = new List<JsonElement>(tools.Count);
        foreach (var declaration in tools.OfType<AIFunctionDeclaration>())
        {
            var wireTool = new Dictionary<string, object>
            {
                ["name"] = declaration.Name,
                ["description"] = declaration.Description,
                ["input_schema"] = declaration.JsonSchema
            };

            var serialized = JsonSerializer.Serialize(wireTool, JsonOptions);
            wireTools.Add(JsonSerializer.Deserialize<JsonElement>(serialized));
        }

        return wireTools;
    }

    private static JsonNode? ToOutputSchema(ChatResponseFormat? responseFormat)
    {
        return responseFormat is ChatResponseFormatJson { Schema: { } schema }
            ? JsonNode.Parse(schema.GetRawText())
            : null;
    }

    /// <summary>
    ///     The API answer as a chat response: <c>tool_use</c> ends with
    ///     <see cref="ChatFinishReason.ToolCalls" /> and one <see cref="FunctionCallContent" /> per
    ///     <c>tool_use</c> block, <c>max_tokens</c> with <see cref="ChatFinishReason.Length" />, anything
    ///     else with <see cref="ChatFinishReason.Stop" /> and the text blocks.
    /// </summary>
    private static ChatResponse ToChatResponse(JsonElement apiResponse, string model)
    {
        var stopReason = apiResponse.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

        var contents = new List<AIContent>();
        JsonElement? rawContent = null;
        if (stopReason == "tool_use")
        {
            // Extract tool use blocks; the content array is kept for the replay of the assistant turn.
            var contentArray = apiResponse.GetProperty("content");
            rawContent = contentArray.Clone();

            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
                {
                    contents.Add(new FunctionCallContent(block.GetProperty("id").GetString()!,
                        block.GetProperty("name").GetString()!, ToArguments(block.GetProperty("input"))));
                }
            }
        }
        else if (apiResponse.TryGetProperty("content", out var finalContent) &&
                 finalContent.ValueKind == JsonValueKind.Array)
        {
            rawContent = finalContent.Clone();
            foreach (var block in finalContent.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    contents.Add(new TextContent(textEl.GetString()));
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = rawContent })
        {
            ModelId = model,
            FinishReason = stopReason switch
            {
                "tool_use" => ChatFinishReason.ToolCalls,
                "max_tokens" => ChatFinishReason.Length,
                _ => ChatFinishReason.Stop
            },
            Usage = ToUsage(apiResponse),
            RawRepresentation = apiResponse
        };
    }

    /// <summary>The members of a tool input in the order the model wrote them.</summary>
    private static OrderedDictionary<string, object?>? ToArguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var arguments = new OrderedDictionary<string, object?>();
        foreach (var property in input.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }

    private static UsageDetails? ToUsage(JsonElement apiResponse)
    {
        if (!apiResponse.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cacheRead = ReadTokens(usage, "cache_read_input_tokens");
        var cacheCreation = ReadTokens(usage, AiUsageAccumulator.CacheCreationInputTokensCount);
        return new UsageDetails
        {
            InputTokenCount = ReadTokens(usage, "input_tokens") + cacheRead + cacheCreation,
            OutputTokenCount = ReadTokens(usage, "output_tokens"),
            CachedInputTokenCount = cacheRead,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                [AiUsageAccumulator.CacheCreationInputTokensCount] = cacheCreation
            }
        };
    }

    private static long ReadTokens(JsonElement usage, string name)
    {
        return usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : 0;
    }

    /// <summary>
    ///     The user message content with a cached source block (AB#8658): the source part carries an
    ///     <c>ephemeral</c> breakpoint, the question follows in a second block. The concatenated text is
    ///     exactly the single-string prompt, so the model reads the same thing.
    /// </summary>
    internal static object[] BuildCachedSourceContent(string sourcePart, string instructionPart)
    {
        return [BuildTextBlock(sourcePart, true), BuildTextBlock(instructionPart, false)];
    }

    /// <summary>
    ///     The Messages API body for one round (5.10.2026, prompt caching). The system prompt and
    ///     the tool list are the parts that repeat verbatim from round to round and from turn to
    ///     turn of a chat — a tool list alone is thousands of tokens — so both carry an
    ///     <c>ephemeral</c> cache breakpoint: the system prompt as its one block, the tools on
    ///     their LAST entry (a breakpoint caches everything before it in the request, tools come
    ///     first). The model then reuses the processed prefix instead of reading it again, which
    ///     is what the first seconds of every follow-up answer went into. A request without a
    ///     system prompt sends none; without tools, no <c>tools</c> key.
    /// </summary>
    internal static Dictionary<string, object> BuildRequestBody(string model, int maxTokens, double? temperature,
        string? systemPrompt, List<object> messages, List<JsonElement>? mcpTools, JsonNode? outputSchema = null)
    {
        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens
        };

        // Without a temperature the key is left out, so the model's default applies.
        if (temperature is { } sentTemperature)
        {
            requestObj["temperature"] = sentTemperature;
        }

        requestObj["messages"] = messages;

        // AB#8498: constrained decoding (structured outputs) for a jsonSchema node. The schema is
        // the API copy (unsupported keywords removed); the client validates the authored schema.
        if (outputSchema != null)
        {
            requestObj["output_config"] = new Dictionary<string, object>
            {
                ["format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["schema"] = outputSchema.DeepClone()
                }
            };
        }

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            requestObj["system"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" }
                }
            };
        }

        if (mcpTools is { Count: > 0 })
        {
            var tools = new List<object>(mcpTools.Count);
            for (var i = 0; i < mcpTools.Count; i++)
            {
                if (i < mcpTools.Count - 1)
                {
                    tools.Add(mcpTools[i]);
                    continue;
                }

                // The last tool carries the breakpoint: copy its members and add cache_control.
                var last = new Dictionary<string, object>();
                foreach (var property in mcpTools[i].EnumerateObject())
                {
                    last[property.Name] = property.Value;
                }

                last["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" };
                tools.Add(last);
            }

            requestObj["tools"] = tools;
        }

        return requestObj;
    }

    /// <summary>
    /// Turns a non-success Anthropic HTTP response into a clear, cause-first message for the
    /// pipeline-execution audit trail. Rate limit / token quota (429), overloaded (529) and
    /// context-window-exceeded (400) are each spelled out; any other status falls back to the
    /// status code. The raw response body is always appended for diagnostics.
    /// </summary>
    internal static string BuildAnthropicApiErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        var code = (int)statusCode;
        var hint = code switch
        {
            429 => "Anthropic rejected the request with a rate limit / token quota error (HTTP 429). " +
                   "No answer was produced and nothing could be processed — retry later, lower the " +
                   "request frequency, or reduce the request size.",
            529 => "The Anthropic API is temporarily overloaded (HTTP 529). No answer was produced — " +
                   "retry later.",
            >= 300 and < 400 =>
                $"The provider answered with a redirect (HTTP {code}). Redirects are not followed, because the " +
                "API key would go along; set the baseUrl of the AiConfiguration to the endpoint that answers.",
            400 when IsContextLengthError(responseBody) =>
                "Anthropic rejected the request because the input exceeds the model's context window " +
                "(HTTP 400). Reduce the input size (e.g. a smaller batch or less context) and retry.",
            _ => $"Anthropic API error (HTTP {code} {statusCode})."
        };

        return $"{hint} Response body: {responseBody}";
    }

    /// <summary>
    ///     An HTTP 400 that refuses the temperature: an <c>invalid_request_error</c> whose message names the
    ///     temperature. Newer models accept no temperature, or only their default; the message is the only
    ///     place the API says so.
    /// </summary>
    internal static bool IsSamplingRejection(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                   type.GetString() == "invalid_request_error" &&
                   error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                   message.GetString()!.Contains("temperature", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Heuristic for an Anthropic HTTP 400 that means "the prompt is larger than the context
    /// window" (an <c>invalid_request_error</c> whose message mentions the prompt being too long /
    /// the context window / the maximum length), as opposed to any other bad-request cause.
    /// </summary>
    internal static bool IsContextLengthError(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        return responseBody.Contains("too long", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("context window", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("context length", StringComparison.OrdinalIgnoreCase);
    }
}

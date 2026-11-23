using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatResponseFormat = OpenAI.Chat.ChatResponseFormat;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The OpenAI Chat Completions API as a chat client of Microsoft.Extensions.AI, built on the official OpenAI
///     .NET library and its Microsoft.Extensions.AI adapter. Serves every endpoint that speaks that API at
///     <c>{baseUrl}/chat/completions</c>: OpenAI (<c>https://api.openai.com/v1</c>), Azure OpenAI
///     (<c>https://&lt;resource&gt;.openai.azure.com/openai/v1</c>, the model is the deployment name), Ollama
///     (<c>/v1</c>) and vLLM. One request per call: the <see cref="LlmQueryEngine" /> owns the tool loop and the
///     retries.
/// </summary>
/// <remarks>
///     The glue around the library, each point pinned by a test:
///     <list type="bullet">
///         <item>Transport: the adapter's provider HTTP client (no redirects, no cookies), no retries of the
///         library, a network timeout of 10 minutes (the library's default is 100 s), library logging off and a
///         fixed User-Agent, so no runtime or operating system details leave the adapter.</item>
///         <item>The key goes in <c>Authorization: Bearer</c>; a keyless endpoint gets no Authorization
///         header.</item>
///         <item>The text parts of a message are sent as one text; cache breakpoints have no meaning here.</item>
///         <item>Tools and a JSON schema answer format are handed to the library as written
///         (<see cref="ChatOptions.RawRepresentationFactory" />), so the schemas go out unchanged and no
///         <c>tool_choice</c> is sent. <c>strict</c> is true only when every object of the answer schema lists
///         all its properties in <c>required</c>, the API's condition for strict mode; either way the engine
///         validates the answer.</item>
///         <item>An answer with tool calls ends with <see cref="ChatFinishReason.ToolCalls" />, whatever its
///         <c>finish_reason</c> says (Ollama says <c>stop</c>), except an answer cut at the token limit, which
///         stays <see cref="ChatFinishReason.Length" />. The assistant message keeps the library's own
///         message as its <see cref="AIChatMessage.RawRepresentation" />, so the replay of a tool round sends the
///         arguments as the model wrote them.</item>
///         <item>A failure is thrown as <see cref="HttpRequestException" /> with a cause-first text and the raw
///         answer, or as <see cref="LlmSamplingRejectedException" /> when an HTTP 400 refuses a temperature that
///         was sent. The library's exception is not kept as inner exception.</item>
///         <item>The exact temperature of the engine (<see cref="LlmQueryEngine.ExactTemperatureProperty" />) is
///         not used: the library carries the temperature as a <see cref="float" />, sent as its shortest decimal
///         text (0.1 stays 0.1).</item>
///     </list>
/// </remarks>
internal sealed class OpenAiCompatibleChatClient(
    IHttpClientFactory httpClientFactory,
    string apiKey,
    Uri baseUrl) : IChatClient
{
    /// <summary>The User-Agent of every request, instead of the library's runtime and system details.</summary>
    internal const string UserAgent = "OctoMesh-MeshAdapter";

    /// <summary>How long one request may take; long tool rounds and large answers need minutes.</summary>
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);

    private HttpClient? _httpClient;

    /// <summary>The time one request may take; a test lowers it.</summary>
    internal TimeSpan RequestTimeout { get; init; } = NetworkTimeout;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var model = options?.ModelId ??
                    throw new ArgumentException("The Chat Completions API needs a model.", nameof(options));
        if (options.MaxOutputTokens == null)
        {
            throw new ArgumentException("The Chat Completions API needs an output-token limit.", nameof(options));
        }

        var httpClient = _httpClient ??= httpClientFactory.CreateClient(AnthropicMessagesChatClient.HttpClientName);
        var chatOptions = ToLibraryOptions(options);

        ChatResponse response;
        try
        {
            using var client = CreateChatClient(model, httpClient).AsIChatClient();
            response = await client.GetResponseAsync(ToLibraryMessages(messages), chatOptions, cancellationToken);
        }
        catch (ClientResultException ex)
        {
            throw ToProviderError(ex, options.Temperature != null);
        }

        // An answer cut at the token limit stays cut, also in the middle of a tool call: its arguments are a
        // fragment, and the engine fails it with the truncation message instead of calling the tool.
        var hasToolCalls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any();
        if (hasToolCalls && response.FinishReason != ChatFinishReason.Length)
        {
            response.FinishReason = ChatFinishReason.ToolCalls;
        }

        if (response.RawRepresentation is ChatCompletion completion)
        {
            foreach (var message in response.Messages)
            {
                message.RawRepresentation = new AssistantChatMessage(completion);
            }
        }

        return response;
    }

    /// <summary>Not supported: the engine reads whole answers.</summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The OpenAI-compatible chat client does not stream.");
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

    /// <summary>The library's chat client for one call, on the adapter's transport (see the remarks).</summary>
    private ChatClient CreateChatClient(string model, HttpClient httpClient)
    {
        var keyless = string.IsNullOrEmpty(apiKey);

        // The library needs a credential; for a keyless endpoint the header policy removes the header it sets.
        return new ChatClient(model, new ApiKeyCredential(keyless ? "keyless" : apiKey),
            CreateClientOptions(httpClient, keyless));
    }

    /// <summary>The library's options: the adapter's transport, no retries, the timeout, no logging.</summary>
    internal OpenAIClientOptions CreateClientOptions(HttpClient httpClient, bool keyless)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = baseUrl,
            Transport = new HttpClientPipelineTransport(httpClient),
            RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = RequestTimeout,
            ClientLoggingOptions = new ClientLoggingOptions { EnableLogging = false }
        };
        clientOptions.AddPolicy(new OutgoingHeadersPolicy(keyless), PipelinePosition.BeforeTransport);
        return clientOptions;
    }

    /// <summary>
    ///     A message of several text parts (the engine splits the prompt for Anthropic's prompt cache) as one text,
    ///     so every server gets the plain string form of <c>content</c>; the parts concatenate to the same prompt.
    ///     Every other message goes to the library as it is.
    /// </summary>
    internal static IEnumerable<AIChatMessage> ToLibraryMessages(IEnumerable<AIChatMessage> messages)
    {
        return messages.Select(message =>
            message.RawRepresentation == null && message.Contents.Count > 1 &&
            message.Contents.All(content => content is TextContent)
                ? new AIChatMessage(message.Role, string.Concat(message.Contents.Cast<TextContent>().Select(t => t.Text)))
                : message);
    }

    /// <summary>
    ///     The options as the library gets them: tools and answer schema leave the chat types and go in as
    ///     written (see the remarks); the engine's own properties stay here.
    /// </summary>
    internal static ChatOptions ToLibraryOptions(ChatOptions options)
    {
        var tools = options.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];
        var schema = options.ResponseFormat is ChatResponseFormatJson { Schema: { } jsonSchema }
            ? jsonSchema.GetRawText()
            : null;

        var libraryOptions = options.Clone();
        libraryOptions.Tools = null;
        libraryOptions.ResponseFormat = null;
        libraryOptions.AdditionalProperties = null;
        libraryOptions.RawRepresentationFactory = _ =>
        {
            var completionOptions = new ChatCompletionOptions();
            foreach (var tool in tools)
            {
                completionOptions.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description,
                    BinaryData.FromString(tool.JsonSchema.GetRawText())));
            }

            if (schema != null)
            {
                completionOptions.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("response",
                    BinaryData.FromString(schema), jsonSchemaIsStrict: IsStrictCompatible(JsonNode.Parse(schema)));
            }

            return completionOptions;
        };
        return libraryOptions;
    }

    /// <summary>
    ///     True when strict mode can hold the schema: every object schema with <c>properties</c> lists each of
    ///     them in <c>required</c>. With an optional property the API would refuse the request in strict mode.
    /// </summary>
    internal static bool IsStrictCompatible(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return RequiresEveryProperty(obj) && obj.All(property => IsStrictCompatible(property.Value));
            case JsonArray array:
                return array.All(IsStrictCompatible);
            default:
                return true;
        }
    }

    /// <summary>True when the object schema has no <c>properties</c>, or lists each of them in <c>required</c>.</summary>
    private static bool RequiresEveryProperty(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return true;
        }

        var required = schema["required"] is JsonArray list
            ? list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        return properties.All(property => required.Contains(property.Key));
    }

    /// <summary>The library's failure as the engine expects it (see the remarks).</summary>
    private static HttpRequestException ToProviderError(ClientResultException ex, bool temperatureSent)
    {
        if (ex.Status == 0)
        {
            // No answer at all (connection refused, timeout): the library's text names the cause.
            return new HttpRequestException(ex.Message);
        }

        var body = ex.GetRawResponse()?.Content?.ToString() ?? string.Empty;
        var status = (HttpStatusCode)ex.Status;
        var message = BuildApiErrorMessage(status, body);
        return temperatureSent && TryReadSamplingRejection(status, body, out var providerError)
            ? new LlmSamplingRejectedException(message, providerError)
            : new HttpRequestException(message);
    }

    /// <summary>
    ///     A cause-first message for a non-success answer, for the audit record of the pipeline execution:
    ///     rate limit or quota (429), redirect (3xx) and input over the context window (400) are spelled out,
    ///     any other status falls back to the status code. The raw body is always appended; the engine masks
    ///     the registered secrets of the execution before the message is logged or stored.
    /// </summary>
    internal static string BuildApiErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        var code = (int)statusCode;
        var hint = code switch
        {
            429 => "The provider rejected the request with a rate limit / token quota error (HTTP 429). " +
                   "No answer was produced and nothing could be processed — retry later, lower the " +
                   "request frequency, or reduce the request size.",
            >= 300 and < 400 =>
                $"The provider answered with a redirect (HTTP {code}). Redirects are not followed, because the " +
                "API key would go along; configure the endpoint that answers.",
            400 when IsContextLengthError(responseBody) =>
                "The provider rejected the request because the input exceeds the model's context window " +
                "(HTTP 400). Reduce the input size (e.g. a smaller batch or less context) and retry.",
            _ => $"Provider API error (HTTP {code} {statusCode})."
        };

        return $"{hint} Response body: {responseBody}";
    }

    private static bool IsContextLengthError(string responseBody)
    {
        return AnthropicMessagesChatClient.IsContextLengthError(responseBody) ||
               responseBody.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     An HTTP 400 that refuses the temperature: its error names <c>temperature</c> as the <c>param</c> or in
    ///     the message. The error is the <c>error</c> object of the answer (OpenAI, Ollama) or the answer itself
    ///     (vLLM). <paramref name="providerError" /> is <c>{type}: {message}</c>, or the message alone.
    /// </summary>
    internal static bool TryReadSamplingRejection(HttpStatusCode statusCode, string? responseBody,
        out string? providerError)
    {
        providerError = null;
        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            var message = ReadString(error, "message");
            if (ReadString(error, "param") != "temperature" &&
                !(message?.Contains("temperature", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }

            var type = ReadString(error, "type");
            providerError = string.IsNullOrEmpty(type) ? message : $"{type}: {message}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    ///     The headers every request leaves with: <see cref="UserAgent" /> instead of the library's runtime and
    ///     operating system details, and for a keyless endpoint no Authorization header.
    /// </summary>
    private sealed class OutgoingHeadersPolicy(bool keyless) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            Apply(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Apply(PipelineMessage message)
        {
            message.Request.Headers.Set("User-Agent", UserAgent);
            if (keyless)
            {
                message.Request.Headers.Remove("Authorization");
            }
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Microsoft.Extensions.AI;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The OpenAI-compatible chat client on its own: what the library sends for the chat types the engine hands
///     it, and the glue around the library (finish reason with tool calls, replay of the arguments, the strict
///     rule, keyless requests, no retries, the timeout). Whole node runs are pinned by the OpenAI golden requests.
/// </summary>
public class OpenAiCompatibleChatClientTests
{
    private const string BaseUrl = "https://llm.example.test/v1";

    private const string TextAnswer =
        """{"id":"c1","object":"chat.completion","created":1,"model":"gpt-test","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,"prompt_tokens_details":{"cached_tokens":64}}}""";

    private const string ToolCallsWithStop =
        """{"id":"c2","object":"chat.completion","created":1,"model":"gpt-test","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":"{\"z\":1, \"a\":\"two\",\"m\":{\"q\":1,\"b\":2}}"}},{"id":"call_2","type":"function","function":{"name":"count","arguments":"{}"}}]},"finish_reason":"stop"}]}""";

    private const string TemperatureRefusal =
        """{"error":{"message":"Unsupported value: 'temperature' does not support 0.1 with this model.","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}""";

    private readonly Wire _wire = new();
    private readonly IHttpClientFactory _factory = A.Fake<IHttpClientFactory>();

    public OpenAiCompatibleChatClientTests()
    {
        A.CallTo(() => _factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_wire, false));
    }

    private OpenAiCompatibleChatClient Client(string apiKey = "the-api-key", string baseUrl = BaseUrl) =>
        new(_factory, apiKey, new Uri(baseUrl));

    private static ChatOptions Options() => new()
    {
        ModelId = "gpt-test",
        MaxOutputTokens = 100
    };

    private static void AssertJson(string expected, string actual)
    {
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"expected {expected}\nactual   {actual}");
    }

    [Fact]
    public async Task APlainPrompt_IsOnePostWithBearerKeyFixedUserAgentAndTheKnownBody()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Instructions = "Be brief.";

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST https://llm.example.test/v1/chat/completions", request.Line);
        Assert.Equal("Bearer the-api-key", request.Headers["authorization"]);
        Assert.Equal(OpenAiCompatibleChatClient.UserAgent, request.Headers["user-agent"]);
        AssertJson(
            """{"messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Hi"}],"model":"gpt-test","max_completion_tokens":100}""",
            request.Body);
        A.CallTo(() => _factory.CreateClient(AnthropicMessagesChatClient.HttpClientName)).MustHaveHappenedOnceExactly();

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("Hello", response.Text);
    }

    [Fact]
    public async Task TheTextPartsOfAMessage_AreSentAsOneText_WithoutCacheMarks()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("the source ")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [LlmQueryEngine.CacheBreakpointProperty] = true
                }
            },
            new TextContent("the question")
        ]);

        using var client = Client();
        await client.GetResponseAsync([message], Options(), TestContext.Current.CancellationToken);

        AssertJson("""[{"role":"user","content":"the source the question"}]""",
            JsonNode.Parse(_wire.Requests[0].Body)!["messages"]!.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AKeylessEndpoint_GetsNoAuthorizationHeader(string? apiKey)
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client(apiKey!, "http://127.0.0.1:11434/v1");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], Options(),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST http://127.0.0.1:11434/v1/chat/completions", request.Line);
        Assert.False(request.Headers.ContainsKey("authorization"));
    }

    [Theory]
    [InlineData("https://my-resource.openai.azure.com/openai/v1",
        "https://my-resource.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://my-resource.openai.azure.com/openai/v1/",
        "https://my-resource.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    public async Task TheBaseUrl_IsWhereTheRequestGoes_WithoutAQuery(string baseUrl, string expected)
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client(baseUrl: baseUrl);
        var options = Options();
        options.ModelId = "my-deployment";
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_wire.Requests);
        Assert.Equal($"POST {expected}", request.Line);
        Assert.Equal("my-deployment", JsonNode.Parse(request.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATemperature_IsSentAsConfigured_AndWithoutOneTheBodyCarriesNone()
    {
        _wire.Answers.Enqueue(TextAnswer);
        _wire.Answers.Enqueue(TextAnswer);
        var withTemperature = Options();
        withTemperature.Temperature = 0.1f;
        withTemperature.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [LlmQueryEngine.ExactTemperatureProperty] = 0.1
        };

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], withTemperature,
            TestContext.Current.CancellationToken);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Contains("\"temperature\":0.1", _wire.Requests[0].Body, StringComparison.Ordinal);
        var second = JsonNode.Parse(_wire.Requests[1].Body)!.AsObject();
        Assert.False(second.ContainsKey("temperature"));
        // The engine's own properties stay with the engine.
        Assert.DoesNotContain("\"temperature\":0.1,\"temperature\"", _wire.Requests[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"total":{"type":"number"}},"required":["total"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"total":{"type":"number"},"note":{"type":"string"}},"required":["total"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}}}}},"required":["items"]}""", false)]
    [InlineData("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}}},"required":["items"]}""", true)]
    [InlineData("""{"type":"object","properties":{"x":{"$ref":"#/$defs/d"}},"required":["x"],"$defs":{"d":{"type":"object","properties":{"k":{"type":"string"}}}}}""", false)]
    public async Task AJsonSchema_GoesOutUnchanged_StrictOnlyWhenEveryPropertyIsRequired(string schema, bool strict)
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        using var document = JsonDocument.Parse(schema);
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(document.RootElement.Clone());

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
            TestContext.Current.CancellationToken);

        Assert.Equal(strict, OpenAiCompatibleChatClient.IsStrictCompatible(JsonNode.Parse(schema)));
        var format = JsonNode.Parse(_wire.Requests[0].Body)!["response_format"]!;
        AssertJson(
            $$$"""{"type":"json_schema","json_schema":{"name":"response","schema":{{{schema}}},"strict":{{{(strict ? "true" : "false")}}}}}""",
            format.ToJsonString());
    }

    [Fact]
    public async Task ToolDeclarations_GoOutAsFunctionTools_WithTheirSchemaUnchanged_AndNoToolChoice()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Tools =
        [
            AIFunctionFactory.CreateDeclaration("search", "Finds things",
                JsonSerializer.SerializeToElement(new
                    { type = "object", properties = new { query = new { type = "string", @default = "x" } } })),
            AIFunctionFactory.CreateDeclaration("count", "",
                JsonSerializer.SerializeToElement(new { type = "object" }))
        ];

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
            TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(_wire.Requests[0].Body)!.AsObject();
        AssertJson(
            """[{"type":"function","function":{"name":"search","description":"Finds things","parameters":{"type":"object","properties":{"query":{"type":"string","default":"x"}}}}},{"type":"function","function":{"name":"count","description":"","parameters":{"type":"object"}}}]""",
            body["tools"]!.ToJsonString());
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.False(body.ContainsKey("parallel_tool_calls"));
        Assert.False(body.ContainsKey("stream"));
    }

    [Fact]
    public async Task ToolCallsWithFinishReasonStop_EndWithToolCalls_AndTheReplaySendsTheArgumentsUnchanged()
    {
        _wire.Answers.Enqueue(ToolCallsWithStop);
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var first = await client.GetResponseAsync(messages, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        var calls = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["call_1", "call_2"], calls.Select(c => c.CallId));
        Assert.Equal(["search", "count"], calls.Select(c => c.Name));
        Assert.Equal(["z", "a", "m"], calls[0].Arguments!.Keys);
        Assert.Empty(calls[1].Arguments!);

        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call_1", "3 hits"),
            new FunctionResultContent("call_2", "Error: boom") { Exception = new InvalidOperationException("boom") }
        ]));
        await client.GetResponseAsync(messages, Options(), TestContext.Current.CancellationToken);

        var replayed = JsonNode.Parse(_wire.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal(4, replayed.Count);
        var assistant = replayed[1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var toolCalls = assistant["tool_calls"]!.AsArray();
        // The arguments are the model's own text, white space and order included.
        Assert.Equal("{\"z\":1, \"a\":\"two\",\"m\":{\"q\":1,\"b\":2}}",
            toolCalls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("call_2", toolCalls[1]!["id"]!.GetValue<string>());
        AssertJson("""{"role":"tool","tool_call_id":"call_1","content":"3 hits"}""", replayed[2]!.ToJsonString());
        AssertJson("""{"role":"tool","tool_call_id":"call_2","content":"Error: boom"}""", replayed[3]!.ToJsonString());
    }

    [Fact]
    public async Task ArgumentsThatAreNoJsonObject_ArePassedOnAsNone()
    {
        _wire.Answers.Enqueue(
            """{"id":"c3","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":"{not json"}}]},"finish_reason":"tool_calls"}]}""");

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.True(call.Arguments == null || call.Arguments.Count == 0);
    }

    [Fact]
    public async Task AnAnswerCutAtTheTokenLimit_EndsWithLength()
    {
        _wire.Answers.Enqueue(
            """{"id":"c4","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"{\"a\":"},"finish_reason":"length"}]}""");

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Fact]
    public async Task Usage_CountsCachedTokensAsPartOfTheInput()
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Equal(100, response.Usage!.InputTokenCount);
        Assert.Equal(64, response.Usage.CachedInputTokenCount);
        Assert.Equal(20, response.Usage.OutputTokenCount);
        var usage = new AiUsageAccumulator();
        usage.Add(response.Usage);
        Assert.Equal(36, usage.InputTokens);
        Assert.Equal(64, usage.CacheReadInputTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "Provider API error (HTTP 500")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit / token quota error (HTTP 429)")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Provider API error (HTTP 503")]
    public async Task AFailure_IsExactlyOneRequest_AndThrowsTheClassifiedError(HttpStatusCode status, string hint)
    {
        _wire.Status = status;
        _wire.RetryAfter = true;
        _wire.Answers.Enqueue("""{"error":{"message":"boom","type":"server_error"}}""");
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.Single(_wire.Requests);
        Assert.Contains(hint, exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("""Response body: {"error":{"message":"boom","type":"server_error"}}""", exception.Message,
            StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("""{"error":{"message":"This model's maximum context length is 8192 tokens.","type":"invalid_request_error","code":"context_length_exceeded"}}""")]
    [InlineData("""{"error":{"message":"too big","code":"context_length_exceeded"}}""")]
    public void AnInputOverTheContextWindow_IsNamedAsSuch(string body)
    {
        var message = OpenAiCompatibleChatClient.BuildApiErrorMessage(HttpStatusCode.BadRequest, body);

        Assert.StartsWith("The provider rejected the request because the input exceeds the model's context window",
            message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public void ARedirect_IsNamedAsSuch(HttpStatusCode status)
    {
        var message = OpenAiCompatibleChatClient.BuildApiErrorMessage(status, "");

        Assert.Contains($"redirect (HTTP {(int)status})", message, StringComparison.Ordinal);
        Assert.Contains("configure the endpoint that answers", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTemperature_IsThrownAsASamplingRejection()
    {
        _wire.Status = HttpStatusCode.BadRequest;
        _wire.Answers.Enqueue(TemperatureRefusal);
        var options = Options();
        options.Temperature = 0.1f;

        using var client = Client();
        var exception = await Assert.ThrowsAsync<LlmSamplingRejectedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 400", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "invalid_request_error: Unsupported value: 'temperature' does not support 0.1 with this model.",
            exception.ProviderError);
    }

    [Theory]
    [InlineData("""{"object":"error","message":"temperature must be within [0, 2]","type":"BadRequestError","param":null,"code":400}""", "BadRequestError: temperature must be within [0, 2]")]
    [InlineData("""{"error":{"message":"bad","param":"temperature"}}""", "bad")]
    public void TheRefusalOfOtherServers_IsReadToo(string body, string providerError)
    {
        Assert.True(OpenAiCompatibleChatClient.TryReadSamplingRejection(HttpStatusCode.BadRequest, body,
            out var read));
        Assert.Equal(providerError, read);
    }

    [Theory]
    [InlineData("""{"error":{"message":"messages: roles must alternate","type":"invalid_request_error"}}""")]
    [InlineData("temperature is not supported")]
    [InlineData("")]
    public void AnyOtherBadRequest_IsNoSamplingRejection(string body)
    {
        Assert.False(OpenAiCompatibleChatClient.TryReadSamplingRejection(HttpStatusCode.BadRequest, body, out _));
    }

    [Fact]
    public async Task ARefusalNamingTheTemperature_WithoutATemperatureSent_IsNoSamplingRejection()
    {
        _wire.Status = HttpStatusCode.BadRequest;
        _wire.Answers.Enqueue(TemperatureRefusal);

        using var client = Client();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.IsNotType<LlmSamplingRejectedException>(exception);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("limit")]
    public async Task WithoutModelOrLimit_NothingIsSent(string missing)
    {
        var options = Options();
        if (missing == "model")
        {
            options.ModelId = null;
        }
        else
        {
            options.MaxOutputTokens = null;
        }

        using var client = Client();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
                TestContext.Current.CancellationToken));

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task TheNetworkTimeout_EndsARequestThatTakesTooLong()
    {
        _wire.Delay = TimeSpan.FromSeconds(30);
        _wire.Answers.Enqueue(TextAnswer);

        using var client = new OpenAiCompatibleChatClient(_factory, "the-api-key", new Uri(BaseUrl))
        {
            RequestTimeout = TimeSpan.FromMilliseconds(200)
        };
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Single(_wire.Requests);
        Assert.Equal(TimeSpan.FromMinutes(10), OpenAiCompatibleChatClient.NetworkTimeout);
    }

    [Fact]
    public void Streaming_IsNotOffered()
    {
        using var client = Client();

        Assert.Throws<NotSupportedException>(() =>
            client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));
        Assert.Same(client, client.GetService(typeof(OpenAiCompatibleChatClient)));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }

    private sealed record SentRequest(string Line, Dictionary<string, string> Headers, string Body);

    private sealed class Wire : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        public Queue<string> Answers { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool RetryAfter { get; set; }
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest($"{request.Method} {request.RequestUri}",
                request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value)), body));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Answers.Dequeue(), Encoding.UTF8, "application/json")
            };
            if (RetryAfter)
            {
                response.Headers.Add("Retry-After", "0");
            }

            return response;
        }
    }
}

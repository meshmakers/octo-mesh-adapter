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
    [InlineData("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}}},"required":["items"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false,"allOf":[{"required":["a"]}]}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["a"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"x":{"$ref":"#/$defs/d"}},"required":["x"],"$defs":{"d":{"type":"object","properties":{"k":{"type":"string"}}}}}""", false)]
    [InlineData("""{"type":"object","properties":{"x":{"$ref":"#/$defs/d"}},"required":["x"],"additionalProperties":false,"$defs":{"d":{"type":"object","properties":{"k":{"type":"string"}},"required":["k"],"additionalProperties":false}}}""", true)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","pattern":"^[A-Z]+$","format":"date"},"n":{"type":"number","minimum":0,"exclusiveMaximum":10,"multipleOf":0.5},"l":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":3}},"required":["a","n","l"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"contains":{"type":"string"},"uniqueItems":{"type":"boolean"}},"required":["contains","uniqueItems"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"s":{"type":"string","enum":["minLength","oneOf"]}},"required":["s"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","minLength":1}},"required":["a"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","maxLength":9}},"required":["a"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"object","properties":{},"required":[],"additionalProperties":false,"propertyNames":{"pattern":"^a"}}},"required":["a"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false,"minProperties":1}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false,"maxProperties":1}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"string"},"uniqueItems":true}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","contains":{"type":"string"}}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"string"},"minContains":1}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"string"},"maxContains":1}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","prefixItems":[{"type":"string"}],"unevaluatedItems":false}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"array","items":{"type":"string"}}""", false)]
    [InlineData("""{"anyOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}]}""", false)]
    [InlineData("""{"type":"object","anyOf":[{"properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}]}""", false)]
    public async Task AJsonSchema_GoesOutUnchanged_StrictOnlyWhenStrictModeAcceptsIt(string schema, bool strict)
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

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public async Task ArgumentsThatAreNoJsonObject_FailTheCall_AndNoToolIsCalled(string arguments)
    {
        var answer = JsonNode.Parse(
            """{"id":"c3","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":""}}]},"finish_reason":"tool_calls"}]}""")!;
        answer["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"] = arguments;
        _wire.Answers.Enqueue(answer.ToJsonString());

        using var client = Client();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "The model asked for the tool 'search' with arguments that are not a JSON object; no tool was called.",
            exception.Message);
    }

    [Fact]
    public async Task AToolCallCutAtTheTokenLimit_EndsWithLength_NotWithAToolRound()
    {
        _wire.Answers.Enqueue(
            """{"id":"c5","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":"{\"query\":\"of"}}]},"finish_reason":"length"}]}""");

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLibraryOptions_LogNothing_RetryNothing_AndWaitTenMinutes(bool keyless)
    {
        using var client = Client();
        using var httpClient = new HttpClient(_wire, false);

        var options = client.CreateClientOptions(httpClient, keyless);

        Assert.False(options.ClientLoggingOptions!.EnableLogging);
        Assert.Equal(TimeSpan.FromMinutes(10), options.NetworkTimeout);
        Assert.Equal(new Uri(BaseUrl), options.Endpoint);
        Assert.NotNull(options.RetryPolicy);
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
    [InlineData("""{"error":{"message":"temperature must be within [0, 2]"}}""", "temperature must be within [0, 2]")]
    public void TheRefusalOfOtherServers_IsReadToo(string body, string providerError)
    {
        Assert.True(OpenAiCompatibleChatClient.TryReadSamplingRejection(HttpStatusCode.BadRequest, body,
            out var read));
        Assert.Equal(providerError, read);
    }

    [Theory]
    [InlineData("""{"error":{"message":"messages: roles must alternate","type":"invalid_request_error"}}""")]
    [InlineData("temperature is not supported")]
    [InlineData("""{"error":{"message":"Invalid schema: property 'temperature' is not allowed","type":"invalid_request_error","param":"response_format"}}""")]
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
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "The provider did not answer within the network timeout of 0.2 s. No answer was produced; retry later " +
            "or reduce the request size.", exception.Message);
        Assert.Null(exception.InnerException);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Single(_wire.Requests);
        Assert.Equal(TimeSpan.FromMinutes(10), OpenAiCompatibleChatClient.NetworkTimeout);
    }

    [Fact]
    public async Task ACancellationOfTheCaller_StaysACancellation()
    {
        _wire.Delay = TimeSpan.FromSeconds(30);
        _wire.Answers.Enqueue(TextAnswer);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        using var client = Client();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(), cancellation.Token));
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

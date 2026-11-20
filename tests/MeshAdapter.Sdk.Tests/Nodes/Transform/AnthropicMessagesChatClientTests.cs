using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Microsoft.Extensions.AI;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The chat client of the Anthropic Messages API on its own: what it sends for the chat types the
///     engine hands it, and what it makes of the API's answers. The requests of whole node runs are
///     pinned by <see cref="AnthropicAiQueryNodeGoldenRequestTests" />; these tests name the rules one
///     by one.
/// </summary>
public class AnthropicMessagesChatClientTests
{
    private const string TextAnswer =
        """{"content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","usage":{"input_tokens":11,"output_tokens":7,"cache_read_input_tokens":5,"cache_creation_input_tokens":3}}""";

    private const string SamplingRefusal =
        """{"type":"error","error":{"type":"invalid_request_error","message":"temperature: this model does not support a custom temperature"}}""";

    private readonly Wire _wire = new();
    private readonly IHttpClientFactory _factory = A.Fake<IHttpClientFactory>();

    public AnthropicMessagesChatClientTests()
    {
        A.CallTo(() => _factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_wire, false));
    }

    private AnthropicMessagesChatClient Client() => new(_factory, "the-api-key");

    private static ChatOptions Options() => new()
    {
        ModelId = "claude-test",
        MaxOutputTokens = 100,
        Temperature = 0.1f
    };

    [Fact]
    public async Task APlainPrompt_IsOnePostWithKeyVersionAndTheKnownBody()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Instructions = "Be brief.";

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST https://api.anthropic.com/v1/messages", request.Line);
        Assert.Equal("the-api-key", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        Assert.Equal(
            """{"model":"claude-test","max_tokens":100,"temperature":0.1,"messages":[{"role":"user","content":"Hi"}],"system":[{"type":"text","text":"Be brief.","cache_control":{"type":"ephemeral"}}]}""",
            request.Body);
        A.CallTo(() => _factory.CreateClient(AnthropicMessagesChatClient.HttpClientName)).MustHaveHappenedOnceExactly();

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("Hello", response.Text);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.2)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public void ATemperature_ReachesTheWireAsItWasConfigured(double configured)
    {
        // The chat types carry the temperature as a float; widening it would send 0.1 as 0.10000000149011612.
        Assert.Equal(configured, AnthropicMessagesChatClient.ToWireTemperature((float)configured));
    }

    [Fact]
    public async Task TheExactTemperatureOfTheCaller_WinsOverTheFloat()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Temperature = (float)0.123456789;
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [LlmQueryEngine.ExactTemperatureProperty] = 0.123456789
        };

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options,
            TestContext.Current.CancellationToken);

        Assert.Contains("\"temperature\":0.123456789,", _wire.Requests[0].Body);
    }

    [Fact]
    public async Task ATextPartWithACacheBreakpoint_IsSentAsBlocks_TheMarkedOneWithCacheControl()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("the source")
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

        Assert.Contains(
            """messages":[{"role":"user","content":[{"type":"text","text":"the source","cache_control":{"type":"ephemeral"}},{"type":"text","text":"the question"}]}]""",
            _wire.Requests[0].Body);
    }

    [Fact]
    public async Task AToolRound_IsReplayedAsDelivered_AndTheResultsFollowAsUserBlocks()
    {
        const string assistantContent =
            """[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_1","name":"search","input":{"query":"offen","limit":5}},{"type":"tool_use","id":"toolu_2","name":"count","input":{}}]""";
        _wire.Answers.Enqueue($$"""{"content":{{assistantContent}},"stop_reason":"tool_use"}""");
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var first = await client.GetResponseAsync(messages, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        var calls = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["toolu_1", "toolu_2"], calls.Select(c => c.CallId));
        Assert.Equal(["search", "count"], calls.Select(c => c.Name));
        Assert.Equal(["query", "limit"], calls[0].Arguments!.Keys);
        Assert.Equal("offen", ((JsonElement)calls[0].Arguments!["query"]!).GetString());
        Assert.Empty(calls[1].Arguments!);

        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("toolu_1", "3 hits"),
            new FunctionResultContent("toolu_2", "Error: boom") { Exception = new InvalidOperationException("boom") }
        ]));
        await client.GetResponseAsync(messages, Options(), TestContext.Current.CancellationToken);

        Assert.Contains(
            """messages":[{"role":"user","content":"Q"},{"role":"assistant","content":""" + assistantContent +
            """},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"3 hits"},{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":"Error: boom"}]}]""",
            _wire.Requests[1].Body);
        // One HTTP client serves every round of a chat client.
        A.CallTo(() => _factory.CreateClient(A<string>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AHistoryEntryThatCallsItselfTool_IsStillText_AndKeepsItsRole()
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        await client.GetResponseAsync(
            [new ChatMessage(new ChatRole("tool"), "from the history"), new ChatMessage(ChatRole.User, "Q")],
            Options(), TestContext.Current.CancellationToken);

        Assert.Contains(
            """messages":[{"role":"tool","content":"from the history"},{"role":"user","content":"Q"}]""",
            _wire.Requests[0].Body);
    }

    [Fact]
    public async Task ToolDeclarations_BecomeAnthropicTools_TheLastOneWithTheCacheBreakpoint()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Tools =
        [
            AIFunctionFactory.CreateDeclaration("search", "Finds things",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } })),
            AIFunctionFactory.CreateDeclaration("count", "",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }))
        ];

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
            TestContext.Current.CancellationToken);

        Assert.EndsWith(
            ""","tools":[{"name":"search","description":"Finds things","input_schema":{"type":"object","properties":{"query":{"type":"string"}}}},{"name":"count","description":"","input_schema":{"type":"object","properties":{}},"cache_control":{"type":"ephemeral"}}]}""",
            _wire.Requests[0].Body);
    }

    [Fact]
    public async Task AJsonSchemaResponseFormat_IsSentAsOutputConfig_Unchanged()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"total":{"type":"number"}},"required":["total"],"additionalProperties":false}""");
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone());

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            """output_config":{"format":{"type":"json_schema","schema":{"type":"object","properties":{"total":{"type":"number"}},"required":["total"],"additionalProperties":false}}}""",
            _wire.Requests[0].Body);
    }

    [Fact]
    public async Task AnAnswerCutAtTheTokenLimit_EndsWithLength()
    {
        _wire.Answers.Enqueue("""{"content":[{"type":"text","text":"{\"a\":"}],"stop_reason":"max_tokens"}""");

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Fact]
    public async Task Usage_FollowsTheConventionOfTheChatTypes_AndSumsUpInTheAccumulatorAsBefore()
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(response.Usage);
        // All input: 11 uncached + 5 read from the cache + 3 written to it.
        Assert.Equal(19, response.Usage.InputTokenCount);
        Assert.Equal(7, response.Usage.OutputTokenCount);
        Assert.Equal(5, response.Usage.CachedInputTokenCount);
        Assert.Equal(3, response.Usage.AdditionalCounts![AiUsageAccumulator.CacheCreationInputTokensCount]);

        // The accumulator reads the same numbers from the chat types as it did from the API answer.
        var fromChatTypes = new AiUsageAccumulator();
        fromChatTypes.Add(response.Usage);
        var fromTheAnswer = new AiUsageAccumulator();
        fromTheAnswer.Add(JsonSerializer.Deserialize<JsonElement>(TextAnswer));
        Assert.Equal(fromTheAnswer.ToJson("claude-test").ToJsonString(), fromChatTypes.ToJson("claude-test").ToJsonString());
    }

    [Fact]
    public async Task AnAnswerWithoutUsage_StillCountsAsACall()
    {
        _wire.Answers.Enqueue("""{"content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn"}""");

        using var client = Client();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
            TestContext.Current.CancellationToken);

        Assert.Null(response.Usage);
        var usage = new AiUsageAccumulator();
        usage.Add(response.Usage);
        Assert.Equal(1, usage.Calls);
        Assert.Equal(0, usage.InputTokens);
    }

    [Fact]
    public async Task ARefusedRequest_ThrowsTheClassifiedError()
    {
        _wire.Answers.Enqueue("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");
        _wire.Status = HttpStatusCode.TooManyRequests;

        using var client = Client();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.Contains("rate limit", exception.Message);
        Assert.Contains("HTTP 429", exception.Message);
        Assert.Contains("retry later", exception.Message);
        Assert.Equal(
            AnthropicMessagesChatClient.BuildAnthropicApiErrorMessage(HttpStatusCode.TooManyRequests,
                """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""),
            exception.Message);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("limit")]
    public async Task WithoutModelOrLimit_NothingIsSent(string missing)
    {
        var options = Options();
        switch (missing)
        {
            case "model": options.ModelId = null; break;
            default: options.MaxOutputTokens = null; break;
        }

        using var client = Client();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
                TestContext.Current.CancellationToken));

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task WithoutATemperature_TheBodyCarriesNoTemperatureKey()
    {
        _wire.Answers.Enqueue(TextAnswer);
        var options = Options();
        options.Temperature = null;
        // An exact value without a temperature is not a temperature either.
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["temperature"] = 0.4 };

        using var client = Client();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"model":"claude-test","max_tokens":100,"messages":[{"role":"user","content":"Hi"}]}""",
            Assert.Single(_wire.Requests).Body);
    }

    [Theory]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic",
        "https://my-resource.services.ai.azure.com/anthropic/v1/messages")]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic/",
        "https://my-resource.services.ai.azure.com/anthropic/v1/messages")]
    [InlineData("https://gateway.example.test", "https://gateway.example.test/v1/messages")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/v1/messages")]
    public async Task ABaseUrl_IsWhereTheRequestGoes_WithTheKey(string baseUrl, string expected)
    {
        _wire.Answers.Enqueue(TextAnswer);

        using var client = new AnthropicMessagesChatClient(_factory, "the-api-key", new Uri(baseUrl));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], Options(),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(_wire.Requests);
        Assert.Equal($"POST {expected}", request.Line);
        Assert.Equal("the-api-key", request.Headers["x-api-key"]);
    }

    [Fact]
    public async Task ARefusedTemperature_IsThrownAsASamplingRejection()
    {
        _wire.Status = HttpStatusCode.BadRequest;
        _wire.Answers.Enqueue(SamplingRefusal);

        using var client = Client();
        var exception = await Assert.ThrowsAsync<LlmSamplingRejectedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<HttpRequestException>(exception);
        Assert.Contains("HTTP 400", exception.Message);
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210000 tokens > 200000 maximum"}}""")]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","message":"messages: roles must alternate"}}""")]
    [InlineData("""{"type":"error","error":{"type":"authentication_error","message":"temperature"}}""")]
    [InlineData("temperature is not supported")]
    [InlineData("")]
    public async Task AnyOtherBadRequest_StaysAPlainHttpRequestException(string body)
    {
        _wire.Status = HttpStatusCode.BadRequest;
        _wire.Answers.Enqueue(body);

        using var client = Client();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));

        Assert.IsNotType<LlmSamplingRejectedException>(exception);
    }

    [Fact]
    public async Task ARefusalNamingTheTemperature_WithoutATemperatureSent_IsNoSamplingRejection()
    {
        // Nothing to leave out: a retry would send the same request again.
        _wire.Status = HttpStatusCode.BadRequest;
        _wire.Answers.Enqueue(SamplingRefusal);
        var options = Options();
        options.Temperature = null;

        using var client = Client();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")], options,
                TestContext.Current.CancellationToken));

        Assert.IsNotType<LlmSamplingRejectedException>(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public void ARedirect_IsNamedAsSuch(HttpStatusCode status)
    {
        var message = AnthropicMessagesChatClient.BuildAnthropicApiErrorMessage(status, "");

        Assert.Contains("redirect", message);
        Assert.Contains($"HTTP {(int)status}", message);
        Assert.Contains("baseUrl", message);
    }

    [Fact]
    public void Streaming_IsNotOffered()
    {
        using var client = Client();

        Assert.Throws<NotSupportedException>(() =>
            client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Q")], Options(),
                TestContext.Current.CancellationToken));
        Assert.Same(client, client.GetService(typeof(AnthropicMessagesChatClient)));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }

    private sealed record SentRequest(string Line, Dictionary<string, string> Headers, string Body);

    private sealed class Wire : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        public Queue<string> Answers { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest($"{request.Method} {request.RequestUri}",
                request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value)), body));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Answers.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}

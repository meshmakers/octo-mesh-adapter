using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The shared engine behind the LLM query nodes, run against a scripted chat client: what it asks of
///     a provider, how it runs tool calls, and the rules it adds around them. No request of a real
///     provider appears here; the wire of <c>AnthropicAiQuery@1</c> is pinned by its own tests.
/// </summary>
public class LlmQueryEngineTests
{
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _configuration = A.Fake<IGlobalConfiguration>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly List<string> _log = [];
    private readonly List<string> _keys = [];
    private readonly ScriptedChatClient _chat = new();
    private readonly McpServer _mcp = new();
    private readonly PipelineSecretRegistry _secrets = new();

    public LlmQueryEngineTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns("engineTenant");
        A.CallTo(() => _etlContext.TenantRepository).Returns(A.Fake<ITenantRepository>());
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_configuration);
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_mcp, false));
        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, "mcp-account",
            A<CancellationToken>._)).Returns(Task.FromResult<string?>("mcp-token"));

        // The secret registry of an execution, as a real node context has it.
        A.CallTo(() => _nodeContext.SecretRegistry).Returns(_secrets);
        A.CallTo(() => _nodeContext.RegisterSecret(A<string?>._))
            .Invokes((string? secret) => _secrets.Register(secret));

        A.CallTo(() => _nodeContext.Debug(A<string>._, A<object[]>._))
            .Invokes((string message, object[] _) => _log.Add("debug: " + message));
        A.CallTo(() => _nodeContext.Info(A<string>._, A<object[]>._))
            .Invokes((string message, object[] _) => _log.Add("info: " + message));
        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._))
            .Invokes((string message, object[] _) => _log.Add("warning: " + message));
        A.CallTo(() => _nodeContext.Error(A<string>._, A<object[]>._))
            .Invokes((string message, object[] _) => _log.Add("error: " + message));
    }

    private LlmQueryEngine Engine() => new(_etlContext, _httpClientFactory, _tokenService, key =>
    {
        _keys.Add(key);
        return _chat;
    });

    private static LlmQueryOptions Options() => new()
    {
        NodeDisplayName = "TestQuery@1",
        McpClientName = "OctoMesh-TestQuery",
        DefaultSystemPrompt = "You are a test.",
        ApiKey = "inline-key",
        Model = "any-model",
        MaxTokens = 500,
        Temperature = 0.3,
        Question = "What is it?",
        Path = "$.text",
        TargetPath = "$.answer",
        ResponseFormat = "text",
        MaxToolRounds = 3,
        MaxJsonRepairAttempts = 1
    };

    private static LlmQueryOptions OptionsWithTools() => Options() with
    {
        McpServerUrl = "https://mcp.example.test",
        McpServiceAccountConfigName = "mcp-account",
        McpToolNames = ["search"]
    };

    private async Task<JsonNode> RunAsync(LlmQueryOptions options, string dataJson = """{"text":"the content"}""")
    {
        using var document = JsonDocument.Parse(dataJson);
        using var dataContext = new DataContextImpl(document.RootElement);
        await Engine().RunAsync(options, dataContext, _nodeContext, _next);
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
        return dataContext.Get<JsonNode>("$")!;
    }

    private static ChatResponse Answer(string text, long inputTokens = 0, long outputTokens = 0) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        };

    private static ChatResponse ToolCall(string callId, string name, params (string Name, object Value)[] arguments)
    {
        var values = new OrderedDictionary<string, object?>();
        foreach (var (argumentName, value) in arguments)
        {
            values[argumentName] = JsonSerializer.SerializeToElement(value);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, values)]))
        {
            FinishReason = ChatFinishReason.ToolCalls
        };
    }

    [Fact]
    public async Task AQuery_RunsOnAnyChatClient_WithTheSettingsOfTheNode()
    {
        _chat.Answers.Enqueue(Answer("42", inputTokens: 10, outputTokens: 4));

        var data = await RunAsync(Options() with { UsageOutputPath = "$.usage" });

        Assert.Equal("42", data["answer"]!.GetValue<string>());
        Assert.Equal(["inline-key"], _keys);
        var call = Assert.Single(_chat.Calls);
        Assert.Equal("any-model", call.Options.ModelId);
        Assert.Equal(500, call.Options.MaxOutputTokens);
        Assert.Equal(0.3f, call.Options.Temperature);
        Assert.Equal(0.3, call.Options.AdditionalProperties![LlmQueryEngine.ExactTemperatureProperty]);
        Assert.Equal("You are a test.", call.Options.Instructions);
        Assert.Null(call.Options.Tools);
        Assert.Null(call.Options.ResponseFormat);
        var message = Assert.Single(call.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Contains("the content", message.Text);
        Assert.Contains("What is it?", message.Text);
        Assert.Equal(1, data["usage"]!["calls"]!.GetValue<int>());
        Assert.Equal(10, data["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.Equal(4, data["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.Equal(1, _chat.Disposals);
    }

    [Fact]
    public async Task CacheSource_MarksTheSourcePartOfTheUserMessage_AndOnlyThat()
    {
        _chat.Answers.Enqueue(Answer("42"));
        _chat.Answers.Enqueue(Answer("42"));

        await RunAsync(Options() with { CacheSource = true });
        await RunAsync(Options());

        var parts = _chat.Calls[0].Messages.Single().Contents.Cast<TextContent>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(true, parts[0].AdditionalProperties![LlmQueryEngine.CacheBreakpointProperty]);
        Assert.Contains("the content", parts[0].Text);
        Assert.Null(parts[1].AdditionalProperties);
        Assert.Contains("What is it?", parts[1].Text);

        var plain = Assert.Single(_chat.Calls[1].Messages.Single().Contents);
        Assert.Null(plain.AdditionalProperties);
        Assert.Equal(parts[0].Text + parts[1].Text, ((TextContent)plain).Text);
    }

    [Fact]
    public async Task AToolCall_GoesToMcpWithItsArguments_AndIsLoggedWithTheirNamesOnly()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search", ("query", "geheime Suche"), ("limit", 5)));
        _chat.Answers.Enqueue(Answer("done"));
        _mcp.ToolResult = "3 hits";

        var data = await RunAsync(OptionsWithTools());

        Assert.Equal("done", data["answer"]!.GetValue<string>());
        // Only the allowed tool of the two the server lists is offered.
        Assert.Equal(["search"], _chat.Calls[0].Options.Tools!.Select(t => t.Name));
        Assert.Equal(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"geheime Suche","limit":5}}}""",
            _mcp.Bodies.Single(b => b.Contains("tools/call")));
        Assert.Contains("OctoMesh-TestQuery", _mcp.Bodies[0]);

        Assert.Contains("debug: Executing MCP tool: search(query, limit)", _log);
        Assert.DoesNotContain(_log, line => line.Contains("geheime Suche"));

        // The second round carries the assistant turn and the tool result.
        var secondRound = _chat.Calls[1].Messages;
        Assert.Equal(3, secondRound.Count);
        Assert.Equal(ChatRole.Tool, secondRound[2].Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(secondRound[2].Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("3 hits" + Environment.NewLine, result.Result);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task AFailedToolCall_IsHandedToTheModelAsAnError_AndTheChatGoesOn()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(Answer("sorry"));
        _mcp.ToolError = "no such index";

        var data = await RunAsync(OptionsWithTools());

        Assert.Equal("sorry", data["answer"]!.GetValue<string>());
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(_chat.Calls[1].Messages[2].Contents));
        Assert.Equal("Error: MCP tool error: no such index", result.Result);
        Assert.NotNull(result.Exception);
        Assert.Contains("warning: MCP tool 'search' failed: MCP tool error: no such index", _log);
    }

    [Fact]
    public async Task AToolErrorThatQuotesTheBearer_ReachesTheModelMasked()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(Answer("sorry"));
        _mcp.ToolError = "Unauthorized: Bearer mcp-token has expired";

        await RunAsync(OptionsWithTools());

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(_chat.Calls[1].Messages[2].Contents));
        Assert.Equal("Error: MCP tool error: Unauthorized: Bearer *** has expired", result.Result);
        Assert.DoesNotContain("mcp-token", result.Exception!.ToString());
    }

    [Fact]
    public async Task AToolResultThatEchoesTheBearer_ReachesTheModelMasked()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(Answer("done"));
        _mcp.ToolResult = "called with Bearer mcp-token";

        await RunAsync(OptionsWithTools());

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(_chat.Calls[1].Messages[2].Contents));
        Assert.Equal("called with Bearer ***" + Environment.NewLine, result.Result);
    }

    [Fact]
    public async Task AToolThatWasNotOffered_IsNeverCalled_AndTheModelIsToldSo()
    {
        // The server lists "delete" too, but the allowlist of the node offers only "search".
        _chat.Answers.Enqueue(ToolCall("call-1", "delete", ("id", "42")));
        _chat.Answers.Enqueue(Answer("sorry"));

        var data = await RunAsync(OptionsWithTools());

        Assert.Equal("sorry", data["answer"]!.GetValue<string>());
        Assert.DoesNotContain(_mcp.Bodies, body => body.Contains("tools/call"));
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(_chat.Calls[1].Messages[2].Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("Error: the tool 'delete' is not available.", result.Result);
        Assert.NotNull(result.Exception);
        Assert.Contains(_log, line => line.StartsWith("warning: The model asked for the tool 'delete'"));
    }

    [Fact]
    public async Task WhenTheToolRoundsAreSpent_NoFurtherBatchRuns_AndTheQueryFails()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(ToolCall("call-2", "search"));

        await RunAsync(OptionsWithTools() with { MaxToolRounds = 1, ContinueOnError = true });

        // One round is allowed: its batch runs, the batch the model asks for afterwards does not.
        Assert.Single(_mcp.Bodies, body => body.Contains("tools/call"));
        Assert.Equal(2, _chat.Calls.Count);
        Assert.Contains("error: Error during AI query: Claude exceeded maximum tool rounds (1)", _log);
    }

    [Fact]
    public async Task WhenTheQueryIsSkippedForMissingContent_AFailureOfALaterNodeIsItsOwn_AndTheChainRunsOnce()
    {
        var downstream = new InvalidOperationException("a later node failed");
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).Throws(downstream);
        using var document = JsonDocument.Parse("{}");
        using var dataContext = new DataContextImpl(document.RootElement);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine().RunAsync(Options() with { ContinueOnError = true }, dataContext, _nodeContext, _next));

        Assert.Same(downstream, thrown);
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustHaveHappenedOnceExactly();
        Assert.Empty(_chat.Calls);
        Assert.DoesNotContain(_log, line => line.StartsWith("error: Error during AI query"));
    }

    [Fact]
    public async Task AToolResultOverTheNodesCap_IsCutAndMarked()
    {
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(Answer("done"));
        _mcp.ToolResult = new string('x', 40);

        await RunAsync(OptionsWithTools() with { MaxToolResultChars = 20 });

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(_chat.Calls[1].Messages[2].Contents));
        var text = Assert.IsType<string>(result.Result);
        Assert.StartsWith(new string('x', 20) + "\n\n[TRUNCATED", text);
        Assert.DoesNotContain(new string('x', 21), text);
        Assert.Contains(_log, line => line.StartsWith("warning: MCP tool 'search' result truncated from ") &&
                                      line.Contains(" to 20 chars."));
    }

    [Fact]
    public void TheDefaultCap_IsTheOneTheAnthropicNodeAlwaysHad()
    {
        Assert.Equal(50_000, Options().MaxToolResultChars);
    }

    [Fact]
    public void AResultWithinTheCap_IsPassedOnUntouched()
    {
        var result = new string('x', 20);

        Assert.Same(result, LlmQueryEngine.CapToolResult(result, "search", 20, _nodeContext));
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("ab\U0001F600cd", 3, "ab")] // the cut would fall between the two halves of the emoji
    [InlineData("ab\U0001F600cd", 4, "ab\U0001F600")]
    [InlineData("ab\U0001F600cd", 2, "ab")]
    [InlineData("abc", 5, "abc")]
    [InlineData("abc", 0, "")]
    public void TheCut_NeverSplitsASurrogatePair(string text, int maxChars, string expected)
    {
        Assert.Equal(expected, LlmQueryEngine.TruncateSurrogateSafe(text, maxChars));
    }

    [Fact]
    public void AToolCallIsDescribed_ByToolAndArgumentNames()
    {
        var withArguments = new FunctionCallContent("1", "search",
            new Dictionary<string, object?> { ["query"] = "geheim", ["limit"] = 5 });

        Assert.Equal("search(query, limit)", LlmQueryEngine.DescribeToolCall(withArguments));
        Assert.Equal("count()", LlmQueryEngine.DescribeToolCall(new FunctionCallContent("2", "count")));
    }

    [Fact]
    public void AMalformedAiConfiguration_EndsInTheNodesOwnSettings_AndTheWarningShowsNothingOfIt()
    {
        A.CallTo(() => _configuration.IsDefined("ai")).Returns(true);
        A.CallTo(() => _configuration.GetRawJson("ai"))
            .Returns("""{"apiKey":"sk-secret-value","aiModel":"configured-model","maxTokens":9000""");
        var options = Options() with { ApiKeyConfigurationName = "ai" };

        Assert.Equal("any-model", LlmQueryEngine.ResolveModel(options, _etlContext, _nodeContext));
        Assert.Equal(500, LlmQueryEngine.ResolveMaxTokens(options, _etlContext, _nodeContext));
        Assert.Equal(0.3, LlmQueryEngine.ResolveTemperature(options, _etlContext, _nodeContext));

        Assert.Equal(3, _log.Count);
        Assert.All(_log, line =>
        {
            Assert.StartsWith("warning: AiConfiguration 'ai' is not valid JSON (line ", line);
            Assert.DoesNotContain("sk-secret-value", line);
            Assert.DoesNotContain("configured-model", line);
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just text\"")]
    [InlineData("null")]
    public void AnAiConfigurationThatIsNoObject_EndsInTheNodesOwnSettings_WithAWarning(string rawJson)
    {
        A.CallTo(() => _configuration.IsDefined("ai")).Returns(true);
        A.CallTo(() => _configuration.GetRawJson("ai")).Returns(rawJson);

        Assert.Equal("any-model",
            LlmQueryEngine.ResolveModel(Options() with { ApiKeyConfigurationName = "ai" }, _etlContext, _nodeContext));
        Assert.Equal(["warning: AiConfiguration 'ai' is not a JSON object."], _log);
    }

    [Fact]
    public async Task SettingsOfAnotherTypeThanText_AreNoSettings_AndTheNodesOwnApply()
    {
        A.CallTo(() => _configuration.IsDefined("ai")).Returns(true);
        A.CallTo(() => _configuration.GetRawJson("ai"))
            .Returns("""{"apiKey":123,"aiModel":456,"mcpServerUrl":true}""");
        _chat.Answers.Enqueue(Answer("42"));

        var data = await RunAsync(Options() with { ApiKeyConfigurationName = "ai" });

        Assert.Equal("42", data["answer"]!.GetValue<string>());
        Assert.Equal(["inline-key"], _keys);
        Assert.Equal("any-model", _chat.Calls.Single().Options.ModelId);
    }

    [Fact]
    public async Task TheJsonFormat_IsRecognisedInAnySpelling_BeforeAnAnswerIsRefusedAsSimulatedToolCalls()
    {
        _chat.Answers.Enqueue(Answer("""{"example":"<tool_call>"}"""));

        var data = await RunAsync(Options() with { ResponseFormat = "JSON" });

        Assert.Equal("<tool_call>", data["answer"]!["example"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAiConfiguration_EndsInTheNodesOwnSettings(string rawJson)
    {
        A.CallTo(() => _configuration.IsDefined("ai")).Returns(true);
        A.CallTo(() => _configuration.GetRawJson("ai")).Returns(rawJson);

        Assert.Equal("any-model",
            LlmQueryEngine.ResolveModel(Options() with { ApiKeyConfigurationName = "ai" }, _etlContext, _nodeContext));
        Assert.Empty(_log);
    }

    [Fact]
    public async Task WithoutATemperature_TheChatOptionsCarryNone()
    {
        _chat.Answers.Enqueue(Answer("42"));

        await RunAsync(Options() with { Temperature = null });

        var call = Assert.Single(_chat.Calls);
        Assert.Null(call.Temperature);
        Assert.Null(call.ExactTemperature);
    }

    [Fact]
    public async Task ARefusedTemperature_IsAskedOnceMoreWithoutIt_AndTheLaterToolRoundsStayWithoutIt()
    {
        _chat.Refusals.Enqueue(SamplingRefusal());
        _chat.Answers.Enqueue(ToolCall("call-1", "search"));
        _chat.Answers.Enqueue(Answer("done"));

        var data = await RunAsync(OptionsWithTools());

        Assert.Equal("done", data["answer"]!.GetValue<string>());
        Assert.Equal([0.3f, null, null], _chat.Calls.Select(c => c.Temperature));
        Assert.Equal([0.3, null, null], _chat.Calls.Select(c => c.ExactTemperature));
        // The repeat is the same request without the temperature.
        Assert.Equal(_chat.Calls[0].Messages, _chat.Calls[1].Messages);
        Assert.Single(_log, line => line.StartsWith("warning: The model refused the temperature; TestQuery@1",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondRefusal_IsNotRepeated_AndFailsTheQuery()
    {
        _chat.Refusals.Enqueue(SamplingRefusal());
        _chat.Refusals.Enqueue(SamplingRefusal());

        using var document = JsonDocument.Parse("""{"text":"the content"}""");
        using var dataContext = new DataContextImpl(document.RootElement);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Engine().RunAsync(Options(), dataContext, _nodeContext, _next));

        Assert.Equal(2, _chat.Calls.Count);
    }

    [Fact]
    public async Task ARefusalWithoutATemperatureSent_IsNotRepeated()
    {
        _chat.Refusals.Enqueue(SamplingRefusal());

        using var document = JsonDocument.Parse("""{"text":"the content"}""");
        using var dataContext = new DataContextImpl(document.RootElement);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Engine().RunAsync(Options() with { Temperature = null }, dataContext, _nodeContext, _next));

        Assert.Single(_chat.Calls);
        Assert.DoesNotContain(_log, line => line.Contains("refused the temperature", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.3, 0f)]
    [InlineData(null, null)]
    public async Task TheRepairCall_IsAtTemperatureZero_OnlyWhenTheRunSendsATemperature(double? configured,
        float? expectedRepairTemperature)
    {
        _chat.Answers.Enqueue(Answer("""{"title":"x"}"""));
        _chat.Answers.Enqueue(Answer("""{"title":"x","facts":["f"]}"""));

        var data = await RunAsync(SchemaOptions() with { Temperature = configured });

        Assert.Equal("f", data["answer"]!["facts"]![0]!.GetValue<string>());
        Assert.Equal(2, _chat.Calls.Count);
        Assert.Equal(expectedRepairTemperature, _chat.Calls[1].Temperature);
    }

    [Fact]
    public async Task AfterARefusedTemperature_TheRepairCallIsWithoutOne()
    {
        _chat.Refusals.Enqueue(SamplingRefusal());
        _chat.Answers.Enqueue(Answer("""{"title":"x"}"""));
        _chat.Answers.Enqueue(Answer("""{"title":"x","facts":["f"]}"""));

        await RunAsync(SchemaOptions());

        Assert.Equal([0.3f, null, null], _chat.Calls.Select(c => c.Temperature));
        Assert.Single(_log, line => line.Contains("refused the temperature", StringComparison.Ordinal));
    }

    private static LlmQueryOptions SchemaOptions() => Options() with
    {
        ResponseFormat = "json",
        JsonSchema = """{"type":"object","properties":{"title":{"type":"string"},"facts":{"type":"array","items":{"type":"string"}}},"required":["title","facts"]}"""
    };

    private static LlmSamplingRejectedException SamplingRefusal(string? providerError = null) =>
        new("Anthropic API error (HTTP 400 BadRequest). Response body: temperature is not supported",
            providerError ?? "invalid_request_error: `temperature` is deprecated for this model.");

    [Theory]
    [InlineData(true, "Remove the temperature from the node")]
    [InlineData(false, "or move to LlmQuery@1, where the temperature is optional")]
    public async Task TheRefusalWarning_AdvisesOnlyWhatTheNodeCanDo(bool temperatureIsOptional, string advice)
    {
        _chat.Refusals.Enqueue(SamplingRefusal());
        _chat.Answers.Enqueue(Answer("42"));

        await RunAsync(Options() with { TemperatureIsOptional = temperatureIsOptional });

        var warning = Assert.Single(_log, line => line.Contains("refused the temperature", StringComparison.Ordinal));
        Assert.Contains(advice, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRefusalWarning_QuotesTheProvidersWords_OnOneLine()
    {
        _chat.Refusals.Enqueue(SamplingRefusal("invalid_request_error: temperature:\nonly 1.0 is supported"));
        _chat.Answers.Enqueue(Answer("42"));

        await RunAsync(Options());

        var warning = Assert.Single(_log, line => line.Contains("refused the temperature", StringComparison.Ordinal));
        Assert.EndsWith(" Provider answer: invalid_request_error: temperature: only 1.0 is supported", warning,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQuotedProviderError_IsMaskedBeforeItIsCut()
    {
        // A secret that crosses the cap: cut first, its head would stay in the log.
        const string secret = "test-key-echoed-by-the-provider-0123456789abcdef";
        _secrets.Register(secret);
        var providerError = "invalid_request_error: temperature refused for " + new string('x', 242) + " " + secret +
                            " " + new string('y', 400);
        _chat.Refusals.Enqueue(SamplingRefusal(providerError));
        _chat.Answers.Enqueue(Answer("42"));

        await RunAsync(Options());

        var warning = Assert.Single(_log, line => line.Contains("refused the temperature", StringComparison.Ordinal));
        Assert.DoesNotContain("test-key-", warning, StringComparison.Ordinal);
        Assert.Contains(PipelineSecretRegistry.Mask, warning, StringComparison.Ordinal);
        var quoted = warning[(warning.IndexOf("Provider answer: ", StringComparison.Ordinal) + 17)..];
        Assert.Equal(LlmQueryEngine.MaxQuotedProviderErrorChars + 1, quoted.Length);
        Assert.EndsWith("…", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedRequest_IsNotCountedInTheUsage_OnlyTheAnsweredOne()
    {
        _chat.Refusals.Enqueue(SamplingRefusal());
        _chat.Answers.Enqueue(Answer("42", inputTokens: 10, outputTokens: 4));

        var data = await RunAsync(Options() with { UsageOutputPath = "$.usage" });

        Assert.Equal(2, _chat.Calls.Count);
        Assert.Equal(1, data["usage"]!["calls"]!.GetValue<int>());
    }

    /// <summary>One call as the chat client saw it; the options are copied, the engine changes its instance.</summary>
    private sealed record ChatCall(List<ChatMessage> Messages, ChatOptions Options, float? Temperature,
        object? ExactTemperature);

    /// <summary>A chat client that answers from a script and keeps what it was asked.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public Queue<ChatResponse> Answers { get; } = new();

        /// <summary>Thrown, in order, by the first calls before any answer is given.</summary>
        public Queue<Exception> Refusals { get; } = new();

        public List<ChatCall> Calls { get; } = [];
        public int Disposals { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // A copy: the engine keeps adding to its list between the rounds.
            object? exact = null;
            options!.AdditionalProperties?.TryGetValue(LlmQueryEngine.ExactTemperatureProperty, out exact);
            Calls.Add(new ChatCall(messages.ToList(), options, options.Temperature, exact));
            return Refusals.TryDequeue(out var refusal)
                ? Task.FromException<ChatResponse>(refusal)
                : Task.FromResult(Answers.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposals++;
    }

    /// <summary>An MCP server with two tools that answers every tool call the same way.</summary>
    private sealed class McpServer : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string ToolResult { get; set; } = "ok";
        public string? ToolError { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var method = JsonNode.Parse(body)!["method"]!.GetValue<string>();
            var answer = method switch
            {
                "initialize" => """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26"}}""",
                "tools/list" =>
                    """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"search","description":"Finds things","inputSchema":{"type":"object","properties":{"query":{"type":"string"}}}},{"name":"delete","description":"Removes things"}]}}""",
                _ when ToolError != null =>
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 1, ["error"] = new JsonObject { ["message"] = ToolError }
                    }.ToJsonString(),
                _ => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ToolResult })
                    }
                }.ToJsonString()
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            };
        }
    }
}

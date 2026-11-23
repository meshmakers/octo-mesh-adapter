using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     <c>LlmQuery@1</c> as a whole node with a real node context: what it hands the engine, where its
///     requests go (public API or the configured base URL) and how it treats the temperature. The engine's
///     own rules are pinned by <see cref="LlmQueryEngineTests" />, the wire by the golden requests.
/// </summary>
public class LlmQueryNodeTests
{
    private const string AiConfigName = "ai";
    private const string ApiKey = "test-key-llmquery-0001";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _configuration = A.Fake<IGlobalConfiguration>();
    private readonly IPipelineLogger _logger = A.Fake<IPipelineLogger>();
    private readonly ProviderWire _wire = new();
    private string _aiConfiguration = $$"""{"apiKey":"{{ApiKey}}","aiModel":"claude-test"}""";

    public LlmQueryNodeTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => _etlContext.TenantRepository).Returns(A.Fake<ITenantRepository>());
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_configuration);
        A.CallTo(() => _configuration.IsDefined(AiConfigName)).Returns(true);
        A.CallTo(() => _configuration.GetRawJson(AiConfigName)).ReturnsLazily(() => _aiConfiguration);
    }

    private static LlmQueryNodeConfiguration Config() => new()
    {
        ApiKeyConfigurationName = AiConfigName,
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        ResponseFormat = "text"
    };

    private async Task<JsonNode> RunAsync(LlmQueryNodeConfiguration config)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_wire, false));

        using var document = JsonDocument.Parse("""{"text":"some content"}""");
        using var dataContext = new DataContextImpl(document.RootElement);
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                _logger, dataContext)
            .RegisterChildNode("LlmQueryNode", 0, config, dataContext);

        var node = new LlmQueryNode(A.Fake<NodeDelegate>(), _etlContext, factory,
            A.Fake<IServiceAccountTokenService>());
        await node.ProcessObjectAsync(dataContext, nodeContext);
        return dataContext.Get<JsonNode>("$")!;
    }

    private IEnumerable<string> Warnings() =>
        Fake.GetCalls(_logger)
            .Where(c => c.Method.Name.Contains("Warn", StringComparison.Ordinal))
            .SelectMany(c => c.Arguments.OfType<string>());

    [Fact]
    public void ToOptions_MapsTheConfiguration_WithoutAnInlineKey()
    {
        var config = Config() with
        {
            Identity = NodeExecutionIdentity.Caller,
            McpServiceAccountConfigName = "sa",
            McpServerUrl = "https://mcp.example.test",
            McpToolNames = ["query_entities"],
            MaxToolResultChars = 1234,
            Temperature = 0.2,
            PromptTemplateName = "tpl",
            UsageOutputPath = "$.usage",
            CacheSource = true
        };

        var options = LlmQueryNode.ToOptions(config);

        Assert.Equal("LlmQuery@1", options.NodeDisplayName);
        Assert.Equal("OctoMesh-LlmQueryNode", options.McpClientName);
        Assert.Equal(LlmQueryNodeConfiguration.DefaultSystemPrompt, options.DefaultSystemPrompt);
        Assert.Null(options.ApiKey);
        Assert.Equal(AiConfigName, options.ApiKeyConfigurationName);
        Assert.True(options.DelegatesToCaller);
        Assert.True(options.McpDelegateToCaller);
        Assert.Equal(1234, options.MaxToolResultChars);
        Assert.Equal(0.2, options.Temperature);
        Assert.Equal(4096, options.MaxTokens);
        Assert.Equal(10, options.MaxToolRounds);
        Assert.Equal("tpl", options.PromptTemplateName);
        Assert.Equal("$.usage", options.UsageOutputPath);
        Assert.True(options.CacheSource);
        Assert.Equal("$.text", options.Path);
        Assert.Equal("$.out", options.TargetPath);

        Assert.False(LlmQueryNode.ToOptions(Config()).DelegatesToCaller);
        Assert.Null(LlmQueryNode.ToOptions(Config()).Temperature);
    }

    private const string SealedKey = "test-key-sealed-0002";
    private const string FoundryUrl = "https://my-resource.services.ai.azure.com/anthropic";

    /// <summary>The AiConfiguration with the configuration key and a sealed endpoint holding the given text.</summary>
    private static string WithProviderEndpoint(JsonNode? providerEndpoint) =>
        new JsonObject
        {
            ["apiKey"] = ApiKey,
            ["aiModel"] = "claude-test",
            ["providerEndpoint"] = providerEndpoint
        }.ToJsonString();

    private static string Sealed(string baseUrl, string? apiKey) =>
        apiKey == null
            ? new JsonObject { ["baseUrl"] = baseUrl }.ToJsonString()
            : new JsonObject { ["baseUrl"] = baseUrl, ["apiKey"] = apiKey }.ToJsonString();

    private string EverythingLogged() =>
        string.Join("\n", Fake.GetCalls(_logger).SelectMany(c => c.Arguments)
            .SelectMany(a => a is object?[] values ? values : new[] { a }).Select(a => a?.ToString()));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "  ")]
    public async Task WithoutAProviderEndpoint_TheConfigurationKeyGoesToThePublicApi(bool present, string? value)
    {
        if (present)
        {
            _aiConfiguration = WithProviderEndpoint(value == null ? null : JsonValue.Create(value));
        }

        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config());

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST https://api.anthropic.com/v1/messages", request.Line);
        Assert.Equal(ApiKey, request.ApiKey);
    }

    [Theory]
    [InlineData(FoundryUrl)]
    [InlineData(FoundryUrl + "/")]
    public async Task ASealedEndpointWithAKey_GetsOnePostWithItsOwnKey_AndNeverTheConfigurationKey(string baseUrl)
    {
        _aiConfiguration = WithProviderEndpoint(Sealed(baseUrl, SealedKey));
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config());

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        var request = Assert.Single(_wire.Requests);
        Assert.Equal($"POST {FoundryUrl}/v1/messages", request.Line);
        Assert.Equal(SealedKey, request.ApiKey);
        Assert.DoesNotContain(_wire.Requests, r => r.ApiKey == ApiKey);
        // The endpoint is logged, its key is not.
        var logged = EverythingLogged();
        Assert.Contains($"Provider endpoint {FoundryUrl} from the providerEndpoint of AiConfiguration '{AiConfigName}'",
            logged, StringComparison.Ordinal);
        Assert.DoesNotContain(SealedKey, logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AKeylessSealedEndpoint_GetsNoKeyHeader_AndTheConfigurationKeyIsIgnored(string? sealedKey)
    {
        _aiConfiguration = WithProviderEndpoint(Sealed("http://localhost:11434", sealedKey));
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config());

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST http://localhost:11434/v1/messages", request.Line);
        Assert.Null(request.ApiKey);
        Assert.Contains("(no key)", EverythingLogged(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MalformedEndpoints => new()
    {
        // not JSON
        { "{\"baseUrl\": test-key-sealed-0002", "not valid JSON (line" },
        // another JSON shape
        { "[\"" + FoundryUrl + "\"]", "not a JSON object" },
        { "\"" + FoundryUrl + "\"", "not a JSON object" },
        // missing or wrong members
        { "{\"apiKey\":\"test-key-sealed-0002\"}", "'baseUrl' is missing or empty" },
        { "{\"baseUrl\":\"\",\"apiKey\":\"test-key-sealed-0002\"}", "'baseUrl' is missing or empty" },
        { "{\"baseUrl\":42,\"apiKey\":\"test-key-sealed-0002\"}", "'baseUrl' is not a JSON text" },
        { "{\"baseUrl\":\"" + FoundryUrl + "\",\"apiKey\":7}", "'apiKey' is not a JSON text" },
        { "{\"baseUrl\":\"" + FoundryUrl + "\",\"apiKey\":null}", "'apiKey' is not a JSON text" },
        { "{\"baseUrl\":\"" + FoundryUrl + "\",\"apiKey\":\"test-key-sealed-0002\",\"token\":\"x\"}", "a member other than" },
        { "{\"baseUrl\":\"" + FoundryUrl + "\",\"baseUrl\":\"https://evil.example\",\"apiKey\":\"test-key-sealed-0002\"}", "given twice" },
        // the base URL rule
        { "{\"baseUrl\":\"http://llm.example.test\",\"apiKey\":\"test-key-sealed-0002\"}", "without TLS" },
        { "{\"baseUrl\":\"https://llm.example.test/v1?key=test-key-sealed-0002\",\"apiKey\":\"test-key-sealed-0002\"}", "query" }
    };

    [Theory]
    [MemberData(nameof(MalformedEndpoints))]
    public async Task AMalformedSealedEndpoint_FailsTheNode_WithoutARequest_AndWithoutAPartOfTheValue(string value,
        string reason)
    {
        _aiConfiguration = WithProviderEndpoint(value);

        // continueOnError does not turn a broken endpoint into a skipped query.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(Config() with { ContinueOnError = true }));

        Assert.Empty(_wire.Requests);
        Assert.Contains($"providerEndpoint of AiConfiguration '{AiConfigName}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SealedKey, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SealedKey, EverythingLogged(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderEndpointThatIsNoText_FailsTheNode_InsteadOfFallingBackToThePublicApi()
    {
        _aiConfiguration = WithProviderEndpoint(new JsonObject { ["baseUrl"] = FoundryUrl, ["apiKey"] = SealedKey });

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(Config()));

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task WithoutATemperatureAnywhere_TheBodyCarriesNone()
    {
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        await RunAsync(Config());

        Assert.False(JsonNode.Parse(Assert.Single(_wire.Requests).Body)!.AsObject().ContainsKey("temperature"));
    }

    [Fact]
    public async Task TheTemperatureOfTheAiConfiguration_IsSent()
    {
        _aiConfiguration = $$"""{"apiKey":"{{ApiKey}}","aiModel":"claude-test","temperature":0.3}""";
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        await RunAsync(Config());

        Assert.Equal(0.3, JsonNode.Parse(Assert.Single(_wire.Requests).Body)!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task ARefusedTemperature_IsAskedOnceMoreWithoutIt_WithOneWarning_AndTheAnswerIsWritten()
    {
        _aiConfiguration = $$"""{"apiKey":"{{ApiKey}}","aiModel":"claude-test","temperature":0.3}""";
        _wire.Answers.Enqueue((HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"`temperature` is deprecated for this model."}}"""));
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config());

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        Assert.Equal(2, _wire.Requests.Count);
        var first = JsonNode.Parse(_wire.Requests[0].Body)!.AsObject();
        var second = JsonNode.Parse(_wire.Requests[1].Body)!.AsObject();
        Assert.True(first.ContainsKey("temperature"));
        Assert.False(second.ContainsKey("temperature"));
        first.Remove("temperature");
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.Single(Warnings(), w => w.Contains("refused the temperature; LlmQuery@1", StringComparison.Ordinal) &&
            w.EndsWith("Provider answer: invalid_request_error: `temperature` is deprecated for this model.",
                StringComparison.Ordinal));
    }

    /// <summary>The provider: answers from a script and keeps every request.</summary>
    internal sealed class ProviderWire : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();
        public List<(string Line, string? ApiKey, string Body)> Requests { get; } = [];

        public static (HttpStatusCode, string) TextAnswer(string text) =>
            (HttpStatusCode.OK, new JsonObject
            {
                ["stop_reason"] = "end_turn",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
            }.ToJsonString());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(($"{request.Method} {request.RequestUri}",
                request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(", ", keys) : null, body));
            var (status, answer) = Answers.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            };
        }
    }
}

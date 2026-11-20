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

    [Fact]
    public async Task WithoutABaseUrl_TheRequestGoesToThePublicApi()
    {
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config());

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST https://api.anthropic.com/v1/messages", request.Line);
        Assert.Equal(ApiKey, request.ApiKey);
    }

    [Theory]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic")]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic/")]
    public async Task WithABaseUrl_TheRequestGoesThere_WithTheKey_AndNothingToThePublicApi(string baseUrl)
    {
        _wire.Answers.Enqueue(ProviderWire.TextAnswer("the answer"));

        var data = await RunAsync(Config() with { BaseUrl = baseUrl });

        Assert.Equal("the answer", data["out"]!.GetValue<string>());
        var request = Assert.Single(_wire.Requests);
        Assert.Equal("POST https://my-resource.services.ai.azure.com/anthropic/v1/messages", request.Line);
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.DoesNotContain(_wire.Requests, r => r.Line.Contains("api.anthropic.com", StringComparison.Ordinal));
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
            """{"type":"error","error":{"type":"invalid_request_error","message":"temperature: not supported for this model"}}"""));
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
        Assert.Single(Warnings(), w => w.Contains("refused the temperature; LlmQuery@1", StringComparison.Ordinal));
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

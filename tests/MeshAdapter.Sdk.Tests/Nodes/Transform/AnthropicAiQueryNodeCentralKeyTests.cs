using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     AB#8691: <c>AnthropicAiQuery@1</c> with <c>keySource: Central</c> calls the AI service's LLM gateway with the
///     pipeline service account's token and never an API key; every model call of a run (MCP tool rounds and JSON
///     repair calls included) goes there; the gateway's refusals become node failures with their own messages; a node
///     that cannot reach the gateway fails before any request. The gateway and the MCP server are scripted; no paid
///     call is made.
/// </summary>
public class AnthropicAiQueryNodeCentralKeyTests : NodeTestBase
{
    private const string GatewayMessages = AnthropicAiQueryGoldenScenario.GatewayBaseUrl + "/llm/anthropic/v1/messages";
    private const string GatewayCredential = "authorization: Bearer <the gateway token of the pipeline service account>";

    private static IReadOnlyList<string> ProviderRequests(AnthropicAiQueryGoldenScenario scenario) =>
        Regex.Split(scenario.Transcript, "(?==== request )")
            .Where(r => r.Contains(" https://ai.example.test/", StringComparison.Ordinal))
            .ToList();

    // ---- AC1: no x-api-key, the service account's token at the gateway ----

    [Fact]
    public async Task Central_SendsTheGatewayTokenToTheGateway_AndNoApiKey()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();

        await scenario.RunCentralAsync(config);

        var request = Assert.Single(ProviderRequests(scenario));
        Assert.Contains($"POST {GatewayMessages} ===", request);
        Assert.Contains(GatewayCredential, request);
        Assert.DoesNotContain("x-api-key", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-octo-caller-ref: pipeline:", request);
        // The AiConfiguration still supplies the model, never its key.
        Assert.Contains("\"model\":\"claude-sonnet-4-6\"", request);
        Assert.DoesNotContain("api.anthropic.com", scenario.Transcript);
        Assert.DoesNotContain("<the api key of the AiConfiguration>", scenario.Transcript);
    }

    // ---- AC2: tool rounds and repair calls of one execution all go through the gateway ----

    [Fact]
    public async Task Central_EveryToolRound_GoesThroughTheGateway_WithTheGatewayToken()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();

        await scenario.RunCentralAsync(config);

        var requests = ProviderRequests(scenario);
        Assert.Equal(2, requests.Count); // the tool_use round and the final answer
        Assert.All(requests, r =>
        {
            Assert.Contains($"POST {GatewayMessages} ===", r);
            Assert.Contains(GatewayCredential, r);
            Assert.DoesNotContain("x-api-key", r, StringComparison.OrdinalIgnoreCase);
        });
        // The MCP server still gets the delegated MCP token, never the gateway token.
        Assert.Contains("Bearer <the delegated token of the caller>", scenario.Transcript);
        Assert.DoesNotContain("<AN UNEXPECTED CREDENTIAL>", scenario.Transcript);
    }

    [Fact]
    public async Task Central_TheJsonRepairCall_GoesThroughTheGateway_WithTheGatewayToken()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.KnowledgeGeneration();

        await scenario.RunCentralAsync(config);

        var requests = ProviderRequests(scenario);
        Assert.Equal(2, requests.Count); // the answer and the repair call
        Assert.All(requests, r =>
        {
            Assert.Contains(GatewayCredential, r);
            Assert.DoesNotContain("x-api-key", r, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- AC3: the gateway's refusals ----

    [Fact]
    public async Task Central_SpendCapReached_FailsTheNode_NamingCapSpentAndUtcResetDate()
    {
        var ex = await RunAgainstRefusal(HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"octo_spend_cap_reached","message":"Monthly LLM cap of EUR 0.01 reached for this tenant; resets on 2026-12-01 UTC."},"code":"AiSpendCapReached","details":{"capEur":0.01,"spentEur":0.0123,"reservedEur":0,"requestWorstCaseEur":0.5,"resetsAt":"2026-12-01T00:00:00.0000000+00:00"}}""");

        Assert.Contains("Monthly LLM cap of EUR 0.01 reached for this tenant (spent EUR 0.01)", ex);
        Assert.Contains("resets on 2026-12-01 UTC", ex);
        Assert.Contains("Ask a Meshmakers platform admin to raise it", ex);
        Assert.Contains("AiSpendCapReached", ex);
        Assert.DoesNotContain("rate limit", ex, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Central_MeterUnavailable_FailsTheNode_WithItsOwnMessage()
    {
        var ex = await RunAgainstRefusal(HttpStatusCode.ServiceUnavailable,
            """{"type":"error","error":{"type":"api_error","message":"LLM spend cannot be metered right now; the call was refused. Try again later."},"code":"AiMeterUnavailable"}""");

        Assert.Contains("cannot be metered", ex);
        Assert.Contains("fail closed", ex);
        Assert.Contains("AiMeterUnavailable", ex);
        Assert.DoesNotContain("Monthly LLM cap", ex);
    }

    [Fact]
    public async Task Central_OtherGatewayCode_FailsTheNode_NamingTheCode()
    {
        var ex = await RunAgainstRefusal(HttpStatusCode.Forbidden,
            """{"type":"error","error":{"type":"permission_error","message":"The tenant's key source is not the central key."},"code":"AiKeySourceNotCentral"}""");

        Assert.Contains("AiKeySourceNotCentral", ex);
        Assert.Contains("HTTP 403", ex);
        Assert.Contains("not the central key", ex);
    }

    [Fact]
    public void Refusal_ProviderErrorWithoutCode_IsNotAGatewayRefusal()
    {
        var read = LlmGatewayAccess.TryReadRefusal(HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", out _);

        Assert.False(read);
    }

    private static async Task<string> RunAgainstRefusal(HttpStatusCode status, string body)
    {
        var handler = new FixedHandler(status, body);
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler, false));
        var client = new AnthropicMessagesChatClient(factory, "token",
            gateway: new LlmGatewayAccess(new Uri(AnthropicAiQueryGoldenScenario.GatewayBaseUrl), null));

        var ex = await Assert.ThrowsAsync<LlmGatewayRefusedException>(() => client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Hi")],
            new Microsoft.Extensions.AI.ChatOptions { ModelId = "claude-sonnet-4-6", MaxOutputTokens = 10 }));
        Assert.Equal(1, handler.Calls);
        return ex.Message;
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    // ---- Fail closed before any request ----

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, FixedHandler Http,
        IMeshEtlContext EtlContext) Prepare(AnthropicAiQueryNodeConfiguration config, string? gatewayUrl,
            string? token)
    {
        var http = new FixedHandler(HttpStatusCode.OK, """{"stop_reason":"end_turn","content":[{"type":"text","text":"x"}]}""");
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(http, false));
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => etlContext.GetLlmGatewayTokenAsync()).Returns(ValueTask.FromResult(token));

        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("some content");

        var node = new AnthropicAiQueryNode(next, etlContext, factory, A.Fake<IServiceAccountTokenService>(),
            Microsoft.Extensions.Options.Options.Create(new MeshAdapterConfiguration { LlmGatewayUrl = gatewayUrl }));
        return (node, dataContext, nodeContext, http, etlContext);
    }

    private static AnthropicAiQueryNodeConfiguration CentralConfig() => new()
    {
        KeySource = LlmKeySource.Central,
        Model = "claude-sonnet-4-6",
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        ResponseFormat = "text",
        ContinueOnError = true
    };

    [Fact]
    public async Task Central_WithAnInlineApiKey_IsRefusedBeforeAnyRequest()
    {
        var (node, dataContext, nodeContext, http, _) =
            Prepare(CentralConfig() with { ApiKey = "sk-tenant" }, AnthropicAiQueryGoldenScenario.GatewayBaseUrl, "t");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("'apiKey' cannot be combined with keySource Central", ex.Message);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://ai.example.test")]
    [InlineData("https://user:pw@ai.example.test")]
    public async Task Central_WithoutAValidGatewayUrl_IsRefusedBeforeAnyRequest(string? gatewayUrl)
    {
        var (node, dataContext, nodeContext, http, etlContext) = Prepare(CentralConfig(), gatewayUrl, "t");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("LlmGatewayUrl", ex.Message);
        Assert.Equal(0, http.Calls);
        A.CallTo(() => etlContext.GetLlmGatewayTokenAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task Central_WithoutAGatewayToken_IsRefusedBeforeAnyRequest_AndNeverFallsBackToAKey()
    {
        var (node, dataContext, nodeContext, http, _) =
            Prepare(CentralConfig() with { ApiKeyConfigurationName = "AiConfig" },
                AnthropicAiQueryGoldenScenario.GatewayBaseUrl, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("ai.llm.invoke", ex.Message);
        Assert.Equal(0, http.Calls);
    }

    // ---- AC4: the tenant-key path stays as it was ----

    [Fact]
    public async Task Configured_IsTheDefault_AndNeverAsksForAGatewayToken()
    {
        var config = CentralConfig() with { KeySource = LlmKeySource.Configured, ApiKey = "test-key" };
        var (node, dataContext, nodeContext, http, etlContext) =
            Prepare(config, AnthropicAiQueryGoldenScenario.GatewayBaseUrl, "t");

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(LlmKeySource.Configured, new AnthropicAiQueryNodeConfiguration().KeySource);
        Assert.Equal(1, http.Calls);
        A.CallTo(() => etlContext.GetLlmGatewayTokenAsync()).MustNotHaveHappened();
    }
}

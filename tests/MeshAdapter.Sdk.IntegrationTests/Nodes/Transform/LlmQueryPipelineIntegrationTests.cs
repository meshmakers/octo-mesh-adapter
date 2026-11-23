using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using MeshAdapter.Sdk.Tests.Nodes.Transform;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
///     <c>LlmQuery@1</c> as a pipeline: the YAML is read by the adapter's serializer, run by the real
///     orchestrator, and the node is built by the adapter's own container with its provider HTTP client.
///     A pipeline that moves from <c>AnthropicAiQuery@1</c> sends the recorded requests byte for byte; with a
///     sealed provider endpoint in the AiConfiguration the request goes to that host only, with that endpoint's
///     key; a sealed endpoint that breaks the rule fails the node before any request, and an unknown provider
///     fails when the pipeline is read. With provider <c>OpenAiCompatible</c> the Chat Completions requests go to
///     the sealed endpoint only (keyless: without an Authorization header), and a tool round runs through the
///     real orchestrator as the OpenAI golden records it.
/// </summary>
[Collection("Sequential")]
public class LlmQueryPipelineIntegrationTests(AiQueryPipelineFixture fixture)
    : IClassFixture<AiQueryPipelineFixture>
{
    private static readonly OctoObjectId PipelineId = new("000000000000000000009309");

    private const string DocumentAnalysisNode = """
                                                apiKeyConfigurationName: AnthropicAiConfig
                                                    question: "Extract the invoice header. Use null for values the document does not state."
                                                    path: $.document.text
                                                    dataPaths:
                                                      - $.document.fileName
                                                      - $.document.meta
                                                      - $.document.pageCount
                                                      - $.missing
                                                    maxTokens: 2000
                                                    temperature: 0.1
                                                    responseFormat: json
                                                    continueOnError: true
                                                    jsonFormatSample: |-
                                                      {
                                                        "invoiceNumber": "RE-2026-0815",
                                                        "grossTotal": 1190.00
                                                      }
                                                    targetPath: $.ai.result
                                                """;

    [Fact]
    public async Task DocumentAnalysis_AsAnLlmQueryPipeline_SendsTheRequestsOfAnthropicAiQuery()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();

        await RunAsync(scenario, $"""
            transformations:
              - type: LlmQuery@1
                {DocumentAnalysisNode}
            """);

        scenario.AssertMatchesGolden("AnthropicAiQuery", "document-analysis", mayRecord: false);
    }

    [Fact]
    public async Task DocumentAnalysis_WithASealedProviderEndpoint_PostsOnceToThatHost_WithItsKey_AndNothingToThePublicApi()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        scenario.ProviderHost = "my-resource.services.ai.azure.com";
        scenario.Configuration[AnthropicAiQueryGoldenScenario.AiConfigName] =
        (
            """{"apiKey":"test-key-configuration-never-sent","aiModel":"claude-sonnet-4-6","providerEndpoint":"{\"baseUrl\":\"https://my-resource.services.ai.azure.com/anthropic\",\"apiKey\":\"test-key-golden-0001\"}"}""",
            null);

        await RunAsync(scenario, $"""
            transformations:
              - type: LlmQuery@1
                {DocumentAnalysisNode}
            """);

        var transcript = scenario.Transcript;
        Assert.Single(transcript.Split("=== request ").Skip(1));
        Assert.Contains("POST https://my-resource.services.ai.azure.com/anthropic/v1/messages ===", transcript);
        Assert.Contains("x-api-key: <the api key of the AiConfiguration>", transcript);
        Assert.DoesNotContain("api.anthropic.com", transcript);
    }

    [Fact]
    public async Task ASealedProviderEndpointThatBreaksTheRule_FailsTheNode_AndNothingIsSent()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        scenario.Configuration[AnthropicAiQueryGoldenScenario.AiConfigName] =
        (
            """{"apiKey":"test-key-golden-0001","aiModel":"claude-sonnet-4-6","providerEndpoint":"{\"baseUrl\":\"http://llm.example.test\",\"apiKey\":\"test-key-golden-0001\"}"}""",
            null);

        // The node runs with continueOnError: true; a wrong endpoint still fails it.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(scenario, $"""
            transformations:
              - type: LlmQuery@1
                {DocumentAnalysisNode}
            """));

        Assert.Contains($"providerEndpoint of AiConfiguration '{AnthropicAiQueryGoldenScenario.AiConfigName}'", ex.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(scenario.Transcript);
    }

    [Fact]
    public async Task AnUnknownProvider_FailsWhenThePipelineIsRead_AndNothingIsSent()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        fixture.EnsureInitialized();
        fixture.Http.SwitchTo(scenario.CreateWire());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync($"""
                transformations:
                  - type: LlmQuery@1
                    provider: OpenAi
                    {DocumentAnalysisNode}
                """));

        Assert.Contains("OpenAi", ex.ToString(), StringComparison.Ordinal);
        Assert.Empty(scenario.Transcript);
    }

    [Fact]
    public async Task OpenAiCompatible_AtAKeylessSealedEndpoint_PostsOnceToThatHost_WithoutAuthorization()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        scenario.ProviderHost = "127.0.0.1";
        scenario.Configuration[AnthropicAiQueryGoldenScenario.AiConfigName] =
        (
            """{"apiKey":"test-key-configuration-never-sent","aiModel":"qwen2.5:7b-instruct","providerEndpoint":"{\"baseUrl\":\"http://127.0.0.1:11434/v1\"}"}""",
            null);
        scenario.AnthropicAnswers.Clear();
        scenario.AnthropicAnswers.Enqueue(AnthropicAiQueryGoldenScenario.OpenAiText(
            """{"invoiceNumber":"RE-2026-0815","grossTotal":1190.00}""", 812, 41));

        await RunAsync(scenario, $"""
            transformations:
              - type: LlmQuery@1
                provider: OpenAiCompatible
                {DocumentAnalysisNode}
            """);

        var transcript = scenario.Transcript;
        Assert.Single(transcript.Split("=== request ").Skip(1));
        Assert.Contains("POST http://127.0.0.1:11434/v1/chat/completions ===", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization:", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-configuration-never-sent", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("api.anthropic.com", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("api.openai.com", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatible_ChatWithMcpTools_RunsOneToolRound_AsTheGoldenRecordsIt()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();
        scenario.UseOpenAiCompatibleEndpoint(
            """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-golden","choices":[{"index":0,"message":{"role":"assistant","content":"Ich sehe nach.","tool_calls":[{"id":"call_01","type":"function","function":{"name":"query_entities","arguments":"{\"ckTypeId\":\"Basic.Accounting/Invoice\",\"take\":10}"}},{"id":"call_02","type":"function","function":{"name":"get_entity","arguments":"{\"rtId\":\"650000000000000000000009\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1500,"completion_tokens":90,"total_tokens":1590}}""",
            AnthropicAiQueryGoldenScenario.OpenAiText("Offen ist RE-1 mit 120,50 €.", 1700, 25, cached: 1280));

        await RunAsync(scenario, """
            transformations:
              - type: LlmQuery@1
                provider: OpenAiCompatible
                apiKeyConfigurationName: AnthropicAiConfig
                question: "Answer the user's message. Use the tools for every figure you state."
                systemPrompt: "You are the accounting assistant of the tenant. Answer in the user's language."
                dataPaths:
                  - $.input.message
                conversationHistoryPath: $.input.history
                responseFormat: text
                maxTokens: 500
                temperature: 0.3
                maxToolRounds: 6
                mcpServiceAccountConfigName: McpServiceAccount
                identity: Caller
                mcpToolNames:
                  - query_entities
                  - get_entity
                targetPath: $.answer
            """);

        scenario.AssertMatchesGoldenByMeaning("LlmQuery", "openai-chat-mcp-tools");
    }

    private async Task RunAsync(AnthropicAiQueryGoldenScenario scenario, string yaml)
    {
        fixture.EnsureInitialized();
        fixture.Http.SwitchTo(scenario.CreateWire());
        A.CallTo(() => fixture.TokenService.AcquireDelegatedTokenAsync(A<ITenantRepository>._,
                AnthropicAiQueryGoldenScenario.ServiceAccountConfigName, AnthropicAiQueryGoldenScenario.CallerToken,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(AnthropicAiQueryGoldenScenario.DelegatedToken));

        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        IMeshEtlContext etlContext = new MeshEtlContext(AnthropicAiQueryGoldenScenario.Tenant, repository, PipelineId,
            Guid.NewGuid(), new RtEntityId("System/RtDataPipeline", PipelineId), DateTime.UtcNow, null,
            scenario.CreateGlobalConfiguration(), new Dictionary<string, object?>(),
            verifiedPrincipal: scenario.Principal, callerAccessToken: scenario.CallerAccessToken);

        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        var result = await fixture.GetService<IEtlDataOrchestrator>()
            .ExecutePipelineAsync(definition, etlContext, value: JsonNode.Parse(scenario.DataJson)!.AsObject());

        scenario.SetFinalData((result as JsonNode ?? JsonSerializer.SerializeToNode(result))?.ToJsonString());
    }
}

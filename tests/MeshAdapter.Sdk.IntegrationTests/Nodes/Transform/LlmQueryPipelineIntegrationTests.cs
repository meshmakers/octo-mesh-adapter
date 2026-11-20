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
///     base URL the request goes to that host only; a refused setting fails when the pipeline is read and
///     nothing is sent.
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
    public async Task DocumentAnalysis_WithABaseUrl_PostsOnceToThatHost_AndNothingToThePublicApi()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        scenario.ProviderHost = "my-resource.services.ai.azure.com";

        await RunAsync(scenario, $"""
            transformations:
              - type: LlmQuery@1
                baseUrl: https://my-resource.services.ai.azure.com/anthropic
                {DocumentAnalysisNode}
            """);

        var transcript = scenario.Transcript;
        Assert.Single(transcript.Split("=== request ").Skip(1));
        Assert.Contains("POST https://my-resource.services.ai.azure.com/anthropic/v1/messages ===", transcript);
        Assert.Contains("x-api-key: <the api key of the AiConfiguration>", transcript);
        Assert.DoesNotContain("api.anthropic.com", transcript);
    }

    [Theory]
    [InlineData("baseUrl: http://llm.example.test", "baseUrl")]
    [InlineData("provider: OpenAiCompatible", "OpenAiCompatible")]
    public async Task ARefusedSetting_FailsWhenThePipelineIsRead_AndNothingIsSent(string line, string named)
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();
        fixture.EnsureInitialized();
        fixture.Http.SwitchTo(scenario.CreateWire());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync($"""
                transformations:
                  - type: LlmQuery@1
                    {line}
                    {DocumentAnalysisNode}
                """));

        Assert.Contains(named, ex.ToString(), StringComparison.Ordinal);
        Assert.Empty(scenario.Transcript);
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

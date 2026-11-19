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
///     Two recorded pipeline shapes of <c>AnthropicAiQuery@1</c> run as pipelines: the YAML is
///     deserialized by the adapter, executed by the real orchestrator, and the node is built by the
///     adapter's own container with its named HTTP clients. The requests that leave the process are
///     compared byte for byte with the same golden files the node's unit tests use, so a difference
///     between "the node called directly" and "the node in a pipeline" (configuration binding, defaults
///     of the YAML path, the HTTP client pipeline) cannot hide.
///     <para>
///         The Anthropic API and the MCP server are scripted; what the MCP SDK server answers to this
///         node is covered by <see cref="AnthropicAiQueryNodeMcpIdentityIntegrationTests" />.
///     </para>
/// </summary>
[Collection("Sequential")]
public class AnthropicAiQueryPipelineGoldenIntegrationTests(AiQueryPipelineFixture fixture)
    : IClassFixture<AiQueryPipelineFixture>
{
    private static readonly OctoObjectId PipelineId = new("000000000000000000009308");

    [Fact]
    public async Task DocumentAnalysis_AsAPipeline_SendsTheRecordedRequests()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();

        await RunAsync(scenario, """
            transformations:
              - type: AnthropicAiQuery@1
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
            """);

        scenario.AssertMatchesGolden("document-analysis");
    }

    [Fact]
    public async Task ChatWithMcpToolsOnTheCallersIdentity_AsAPipeline_SendsTheRecordedRequests()
    {
        var (scenario, _) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();

        await RunAsync(scenario, """
            transformations:
              - type: AnthropicAiQuery@1
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
                mcpDelegateToCaller: true
                mcpToolNames:
                  - query_entities
                  - get_entity
                targetPath: $.answer
            """);

        scenario.AssertMatchesGolden("chat-mcp-delegation");
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

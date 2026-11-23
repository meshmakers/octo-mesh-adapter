using Meshmakers.Octo.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The requests of <c>LlmQuery@1</c> with provider <c>OpenAiCompatible</c> at a sealed endpoint, for two of the
///     golden pipeline shapes of <see cref="AnthropicAiQueryGoldenScenario" />: knowledge generation (schema and
///     one repair call) and the chat with MCP tools on the caller's identity (one tool round). The OpenAI library
///     writes these requests, so the files are compared by meaning
///     (<see cref="AnthropicAiQueryGoldenScenario.AssertMatchesGoldenByMeaning" />), the convention of the OpenAI
///     goldens; record them again with <c>OCTO_UPDATE_GOLDEN_REQUESTS=1</c> and review the diff.
/// </summary>
public class LlmQueryOpenAiGoldenRequestTests
{
    private const string Folder = "LlmQuery";

    [Fact]
    public async Task KnowledgeGeneration_WithSchemaAndOneRepairCall()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.KnowledgeGeneration();
        scenario.UseOpenAiCompatibleEndpoint(
            AnthropicAiQueryGoldenScenario.OpenAiText("""{"title":"Pipelines"}""", 2100, 12, cached: 1024),
            AnthropicAiQueryGoldenScenario.OpenAiText(
                """{"title":"Pipelines","facts":["Pipelines are YAML-defined."]}""", 260, 22));

        await scenario.RunLlmQueryAsync(config, LlmProvider.OpenAiCompatible);

        scenario.AssertMatchesGoldenByMeaning(Folder, "openai-knowledge-generation");
    }

    [Fact]
    public async Task ChatWithMcpTools_OneToolRound_OnTheCallersIdentity()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();
        scenario.UseOpenAiCompatibleEndpoint(
            """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-golden","choices":[{"index":0,"message":{"role":"assistant","content":"Ich sehe nach.","tool_calls":[{"id":"call_01","type":"function","function":{"name":"query_entities","arguments":"{\"ckTypeId\":\"Basic.Accounting/Invoice\",\"take\":10}"}},{"id":"call_02","type":"function","function":{"name":"get_entity","arguments":"{\"rtId\":\"650000000000000000000009\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1500,"completion_tokens":90,"total_tokens":1590}}""",
            AnthropicAiQueryGoldenScenario.OpenAiText("Offen ist RE-1 mit 120,50 €.", 1700, 25, cached: 1280));

        await scenario.RunLlmQueryAsync(config, LlmProvider.OpenAiCompatible);

        scenario.AssertMatchesGoldenByMeaning(Folder, "openai-chat-mcp-tools");
    }
}

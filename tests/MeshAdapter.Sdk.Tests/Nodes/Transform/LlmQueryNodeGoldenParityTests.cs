namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     <c>LlmQuery@1</c> with provider Anthropic sends what <c>AnthropicAiQuery@1</c> sends: the three
///     golden pipeline shapes of <see cref="AnthropicAiQueryNodeGoldenRequestTests" /> run through the new
///     node on the same scripted wire. Document analysis and knowledge generation compare with the files of
///     <c>AnthropicAiQuery@1</c> byte for byte. The chat with MCP tools has its own file, because the node
///     announces its own client name in the MCP handshake; that name and the length of that one request are
///     the only differences to the file of <c>AnthropicAiQuery@1</c>.
/// </summary>
public class LlmQueryNodeGoldenParityTests
{
    [Fact]
    public async Task DocumentAnalysis_SendsExactlyWhatAnthropicAiQuerySends()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();

        await scenario.RunLlmQueryAsync(config);

        scenario.AssertMatchesGolden("AnthropicAiQuery", "document-analysis", mayRecord: false);
    }

    [Fact]
    public async Task KnowledgeGeneration_SendsExactlyWhatAnthropicAiQuerySends()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.KnowledgeGeneration();

        await scenario.RunLlmQueryAsync(config);

        scenario.AssertMatchesGolden("AnthropicAiQuery", "knowledge-generation", mayRecord: false);
    }

    [Fact]
    public async Task ChatWithMcpTools_OnTheCallersIdentity()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();

        await scenario.RunLlmQueryAsync(config);

        scenario.AssertMatchesGolden("LlmQuery", "chat-mcp-delegation", mayRecord: true);
    }
}

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// LLM provider types supported by <see cref="LlmQueryNodeConfiguration"/>.
/// </summary>
public enum LlmProvider
{
    /// <summary>
    /// The Anthropic Messages API (Claude): api.anthropic.com, or a compatible endpoint such as
    /// Microsoft Foundry set as the sealed <c>providerEndpoint</c> of the AiConfiguration.
    /// </summary>
    Anthropic = 0,

    /// <summary>
    /// The OpenAI Chat Completions API at the sealed <c>providerEndpoint</c> of the AiConfiguration, which is
    /// required: OpenAI, Azure OpenAI, Ollama (/v1), vLLM, LiteLLM and any other endpoint that serves
    /// <c>{baseUrl}/chat/completions</c>.
    /// </summary>
    OpenAiCompatible = 1
}

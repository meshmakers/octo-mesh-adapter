namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// LLM provider types supported by <see cref="LlmQueryNodeConfiguration"/>.
/// </summary>
public enum LlmProvider
{
    /// <summary>
    /// Any endpoint exposing the OpenAI HTTP API: OpenAI cloud, Azure OpenAI,
    /// and self-hosted backends (Ollama, vLLM, Cerebras, LiteLLM, ...).
    /// </summary>
    OpenAiCompatible,

    /// <summary>
    /// Native Anthropic API (Claude).
    /// </summary>
    Anthropic
}

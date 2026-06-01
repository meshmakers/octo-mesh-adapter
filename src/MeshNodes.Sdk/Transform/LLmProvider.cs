namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// LLM provider types supported by <see cref="LlmQueryNodeConfiguration"/>.
/// </summary>
public enum LlmProvider
{
    /// <summary>
    ///     <term>OpenAICompatible</term>
    ///     <description>OpenAI, Azure OpenAI, Ollama (/v1/), vLLM, TGI, LiteLLM
    ///     proxy, AWS Bedrock OpenAI-gateway, KServe, LocalAI, and any custom
    ///     self-hosted endpoint that exposes the OpenAI HTTP API.</description>
    /// </summary>
    OpenAiCompatible,
    /// <summary>
    ///     <term>Anthropic</term>
    ///     <description>Native Anthropic API (Claude direct).</description>
    /// </summary>
    Anthropic   // placeholder for Spike 4
}
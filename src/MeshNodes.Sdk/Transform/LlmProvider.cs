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
    Anthropic
}

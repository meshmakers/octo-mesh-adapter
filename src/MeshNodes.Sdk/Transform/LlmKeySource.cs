namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Where <see cref="AnthropicAiQueryNodeConfiguration"/> gets the right to call the model from (AB#8691).
/// </summary>
public enum LlmKeySource
{
    /// <summary>
    /// The tenant's own key: <c>apiKeyConfigurationName</c> (AiConfiguration) or <c>apiKey</c>, sent straight to
    /// the provider. The default and the behaviour of every node written before the central key existed.
    /// </summary>
    Configured = 0,

    /// <summary>
    /// The central Meshmakers key, held only by the AI service's LLM gateway. The node sends no API key; it
    /// authenticates at the gateway with the pipeline service account's token (scope <c>ai.llm.invoke</c>), and the
    /// gateway meters the call against the tenant's monthly cap and refuses it once the cap is used up
    /// (<c>AiSpendCapReached</c>). Needs the adapter setting <c>Adapter:LlmGatewayUrl</c> and a tenant whose key
    /// source is <c>CentralKey</c> in the platform's cap store.
    /// </summary>
    Central = 1
}

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The provider refused the request because of its sampling settings (newer models accept no
///     temperature, or only their default). A chat client throws it only for a refusal it classified as
///     such; the <see cref="LlmQueryEngine" /> then asks once more without a temperature.
/// </summary>
/// <param name="message">The full error text of the call (status and response body).</param>
/// <param name="providerError">
///     The provider's own words, <c>{type}: {message}</c> of its error object; not masked yet, the engine masks
///     it before it is logged.
/// </param>
internal sealed class LlmSamplingRejectedException(string message, string? providerError = null)
    : HttpRequestException(message)
{
    /// <summary>The provider's error type and message, as it sent them.</summary>
    public string? ProviderError { get; } = providerError;
}

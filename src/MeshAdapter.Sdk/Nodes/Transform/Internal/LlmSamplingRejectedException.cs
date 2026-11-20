namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The provider refused the request because of its sampling settings (newer models accept no
///     temperature, or only their default). A chat client throws it only for a refusal it classified as
///     such; the <see cref="LlmQueryEngine" /> then asks once more without a temperature.
/// </summary>
internal sealed class LlmSamplingRejectedException(string message) : HttpRequestException(message);

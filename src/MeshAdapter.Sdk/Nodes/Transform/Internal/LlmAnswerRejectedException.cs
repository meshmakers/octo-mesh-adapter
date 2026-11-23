using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The provider answered, but the chat client refuses to hand the answer on (e.g. a tool call whose
///     arguments are not a JSON object). The answer was billed, so it carries the answer's usage; the
///     <see cref="LlmQueryEngine" /> counts it before the query fails.
/// </summary>
/// <param name="message">Why the answer is refused.</param>
/// <param name="usage">The usage of the refused answer, as the provider reported it.</param>
internal sealed class LlmAnswerRejectedException(string message, UsageDetails? usage)
    : InvalidOperationException(message)
{
    /// <summary>The usage of the refused answer; null when the provider reported none.</summary>
    public UsageDetails? Usage { get; } = usage;
}

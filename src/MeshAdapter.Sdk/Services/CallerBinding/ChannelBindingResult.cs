using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The verdict a channel trigger acts on after applying its <c>CallerBinding</c> policy to a
///     sender (AB#5126): either run the pipeline (optionally as a resolved caller) or refuse it.
/// </summary>
/// <param name="Rejected">
///     <c>true</c> when the binding was required but the sender did not resolve — the trigger must
///     NOT execute the pipeline and must NOT downgrade to the service account.
/// </param>
/// <param name="RejectReason">A human-readable reason when <paramref name="Rejected" />; else null.</param>
/// <param name="Principal">
///     The resolved caller to put on the execution, or null to run anonymously (as the service
///     account). Always null when <paramref name="Rejected" />.
/// </param>
/// <param name="Trust">The effective trust to carry on the execution; <see cref="CallerTrustLevel.None" /> when anonymous.</param>
public sealed record ChannelBindingResult(
    bool Rejected,
    string? RejectReason,
    VerifiedPrincipal? Principal,
    CallerTrustLevel Trust)
{
    /// <summary>Run the pipeline anonymously — no resolved caller (the service-account / anonymous path).</summary>
    public static readonly ChannelBindingResult Anonymous = new(false, null, null, CallerTrustLevel.None);

    /// <summary>Run the pipeline as the resolved caller with its effective trust.</summary>
    public static ChannelBindingResult Caller(VerifiedPrincipal principal, CallerTrustLevel trust)
        => new(false, null, principal, trust);

    /// <summary>Refuse the execution: a required binding could not be satisfied.</summary>
    public static ChannelBindingResult Reject(string reason)
        => new(true, reason, null, CallerTrustLevel.None);
}

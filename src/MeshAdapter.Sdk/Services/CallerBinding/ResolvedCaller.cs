using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The result of resolving a <see cref="ChannelSender" /> against the verified-identifier
///     directory (AB#5126): the token-free caller principal plus the effective trust of the binding
///     (<c>min(enrollment, message)</c>). Returned by <see cref="IVerifiedCallerDirectory" /> when a
///     binding exists.
/// </summary>
/// <param name="Principal">The resolved, token-free caller (mapped from the directory's user).</param>
/// <param name="EffectiveTrust">The effective trust the caller was resolved with.</param>
public sealed record ResolvedCaller(
    VerifiedPrincipal Principal,
    CallerTrustLevel EffectiveTrust);

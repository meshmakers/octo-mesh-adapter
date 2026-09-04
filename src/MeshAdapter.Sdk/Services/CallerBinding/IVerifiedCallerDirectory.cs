using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The generic <b>directory-lookup by external identifier</b> seam (AB#5126): resolves a
///     <see cref="ChannelSender" /> to a verified caller, independent of which channel produced it.
///     This is the adapter-side counterpart of the identity directory's <c>IVerifiedIdentifierResolver</c>
///     (AB#5122) — modelled as a channel-neutral seam here because the adapter SDK cannot reference
///     the identity persistence assembly.
/// </summary>
/// <remarks>
///     🔴 <b>Wiring extension point.</b> AB#5126 defines this seam and ships the fail-closed default
///     <see cref="UnboundVerifiedCallerDirectory" /> (always "unresolved"), so channels behave exactly
///     as before this WI until the directory is wired: <see cref="CallerBindingMode.BindingOptional" />
///     and <see cref="CallerBindingMode.AnonymousAllowed" /> run as the service account, and
///     <see cref="CallerBindingMode.BindingRequired" /> refuses. The concrete implementation — bridging
///     to the AB#5122 directory (through generic CK entity access over the tenant repository, or a
///     service-to-service call to identity) — lands with the per-channel resolution WIs (AB#5123/5124/5125),
///     which also supply the real per-message trust on the <see cref="ChannelSender" />.
/// </remarks>
public interface IVerifiedCallerDirectory
{
    /// <summary>
    ///     Resolves <paramref name="sender" /> in <paramref name="tenantId" />'s directory to a
    ///     verified caller, or returns <c>null</c> when no binding exists.
    /// </summary>
    Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default);
}

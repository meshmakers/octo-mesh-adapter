using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The fail-closed default <see cref="IVerifiedCallerDirectory" /> shipped by AB#5126: it
///     resolves <b>nothing</b>. It exists so the caller-binding seam is wired end to end — every
///     channel trigger, the decision logic and the execution context all carry a caller and trust —
///     while the actual directory bridge is still the work of the per-channel WIs (AB#5123/5124/5125).
/// </summary>
/// <remarks>
///     With this default in place the observable behaviour is exactly the pre-AB#5126 behaviour:
///     <see cref="CallerBindingMode.AnonymousAllowed" /> and <see cref="CallerBindingMode.BindingOptional" />
///     both run as the service account (no caller resolved), and only the deliberately strict
///     <see cref="CallerBindingMode.BindingRequired" /> changes anything — it refuses, which is the
///     correct fail-closed answer while no directory can vouch for any sender.
/// </remarks>
internal sealed class UnboundVerifiedCallerDirectory(ILogger<UnboundVerifiedCallerDirectory> logger)
    : IVerifiedCallerDirectory
{
    public Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default)
    {
        // Debug, not warning: an unresolved sender is the normal state until a per-channel WI wires
        // the directory, and a busy mailbox would otherwise flood the log.
        logger.LogDebug(
            "[{TenantId}] No verified-caller directory is wired; sender {Kind} cannot be resolved (AB#5126 seam — AB#5123/5124/5125 supply the binding)",
            tenantId, sender.Kind);
        return Task.FromResult<ResolvedCaller?>(null);
    }
}

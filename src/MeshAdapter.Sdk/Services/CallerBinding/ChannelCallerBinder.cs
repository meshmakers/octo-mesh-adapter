using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The one place a channel trigger turns its <see cref="CallerBindingMode" /> + the message
///     sender into a <see cref="ChannelBindingResult" /> (AB#5126). It combines the directory lookup
///     (<see cref="IVerifiedCallerDirectory" />) with the pure three-state decision
///     (<see cref="CallerBindingDecision" />) so every channel trigger enforces the policy
///     identically — the channel-side counterpart of what <c>PipelineIdentityResolver</c> does for
///     HTTP.
/// </summary>
public interface IChannelCallerBinder
{
    /// <summary>
    ///     Applies <paramref name="mode" /> to <paramref name="sender" /> for <paramref name="tenantId" />.
    ///     <paramref name="sender" /> is null when the trigger has no single sender for this execution
    ///     (an internal fire, or a batch spanning several senders) — treated the same as an unresolved
    ///     sender.
    /// </summary>
    Task<ChannelBindingResult> BindAsync(string tenantId, CallerBindingMode mode, ChannelSender? sender,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class ChannelCallerBinder(
    IVerifiedCallerDirectory directory,
    ILogger<ChannelCallerBinder> logger) : IChannelCallerBinder
{
    public async Task<ChannelBindingResult> BindAsync(string tenantId, CallerBindingMode mode,
        ChannelSender? sender, CancellationToken cancellationToken = default)
    {
        // AnonymousAllowed never even looks — anonymous is a choice, not a failed lookup (AB#5126).
        // This also means the pre-AB#5126 default (mode 0) costs no directory round trip.
        ResolvedCaller? resolved = null;
        if (CallerBindingDecision.ShouldAttemptResolution(mode) && sender != null)
        {
            resolved = await directory.ResolveAsync(tenantId, sender, cancellationToken);
        }

        var outcome = CallerBindingDecision.Decide(mode, resolved != null);

        switch (outcome)
        {
            case CallerBindingOutcome.UseResolvedCaller:
                logger.LogDebug(
                    "[{TenantId}] Channel trigger runs as resolved caller '{SubjectId}' (trust {Trust}, mode {Mode})",
                    tenantId, resolved!.Principal.SubjectId, resolved.EffectiveTrust, mode);
                return ChannelBindingResult.Caller(resolved.Principal, resolved.EffectiveTrust);

            case CallerBindingOutcome.Reject:
                var reason = sender == null
                    ? "Caller binding is required, but this trigger produced no single identifiable sender for the execution."
                    : $"Caller binding is required, but the sender ({sender.Kind}) could not be resolved to a verified identity.";
                logger.LogWarning("[{TenantId}] Rejecting channel execution: {Reason}", tenantId, reason);
                return ChannelBindingResult.Reject(reason);

            default:
                return ChannelBindingResult.Anonymous;
        }
    }
}

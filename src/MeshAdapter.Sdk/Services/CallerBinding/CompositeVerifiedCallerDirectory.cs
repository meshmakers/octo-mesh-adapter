using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The single <see cref="IVerifiedCallerDirectory" /> the channel binder consumes (AB#5123),
///     dispatching a <see cref="ChannelSender" /> by its <see cref="ChannelSender.Kind" /> to the
///     per-kind leaf directories (<see cref="IKindVerifiedCallerDirectory" />) that own it. This is the
///     refactor AB#5124 flagged as needed: EntraID (AB#5124) and phone (AB#5123) now COEXIST — each is
///     a leaf owning its own kind — and e-mail (AB#5125) slots in as another leaf with no change here
///     or at the binder.
/// </summary>
/// <remarks>
///     Stays fail-closed: a kind no leaf owns resolves to <c>null</c>, so the binder runs the trigger
///     as the service account under the permissive modes and refuses under <c>BindingRequired</c>,
///     exactly as before any directory was wired (AB#5126). When more than one leaf owns a kind the
///     first non-null resolution wins (registration order).
/// </remarks>
internal sealed class CompositeVerifiedCallerDirectory(
    IEnumerable<IKindVerifiedCallerDirectory> directories,
    ILogger<CompositeVerifiedCallerDirectory> logger) : IVerifiedCallerDirectory
{
    private readonly IReadOnlyList<IKindVerifiedCallerDirectory> _directories = directories.ToList();

    public async Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default)
    {
        var owningDirectories = _directories.Where(d => d.Owns(sender.Kind)).ToList();
        if (owningDirectories.Count == 0)
        {
            logger.LogDebug(
                "[{TenantId}] No verified-caller directory owns sender kind {Kind}; sender is unresolved",
                tenantId, sender.Kind);
            return null;
        }

        foreach (var directory in owningDirectories)
        {
            var resolved = await directory.ResolveAsync(tenantId, sender, cancellationToken);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }
}

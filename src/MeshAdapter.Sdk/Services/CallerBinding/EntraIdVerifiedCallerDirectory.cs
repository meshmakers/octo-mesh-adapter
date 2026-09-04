using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The real <see cref="IVerifiedCallerDirectory" /> for the Teams / EntraID path (AB#5124): it
///     resolves a Teams sender's AAD object id (<see cref="ChannelIdentifierKind.EntraIdObjectId" />)
///     to the OctoMesh user the EntraID identity provider already provisioned, through the AB#5122
///     verified-identifier directory. This is <b>not</b> a new enrollment — when the tenant runs an
///     EntraID IdP, a Teams user IS an OctoMesh user (same <c>oid</c> subject), and the IdP login
///     records the <c>(EntraIdObjectId, oid) → user</c> binding (source = IdentityProvider). This
///     directory reads it.
/// </summary>
/// <remarks>
///     <para>
///         Replaces the fail-closed <see cref="UnboundVerifiedCallerDirectory" /> in the adapter DI
///         (AB#5126 seam). It stays fail-closed for every kind it does not own: a non-EntraID sender
///         (phone / e-mail — the sibling WIs AB#5123/5125) resolves to <c>null</c> here, so the
///         binder falls back to the service account under the permissive modes and rejects under
///         <c>BindingRequired</c>, exactly as before. When those WIs land, a composite directory can
///         dispatch by kind; today a single EntraID directory is the whole surface.
///     </para>
///     <para>
///         <b>Trust.</b> The effective trust is <c>min(enrollmentTrust, messageTrust)</c> — the same
///         rule AB#5122's resolver applies. The enrollment dimension is stored on the binding (the
///         IdP writes it Strong); the message dimension is what the Teams trigger vouched for THIS
///         activity (Strong only when the inbound Bot Framework token was cryptographically
///         validated — see <c>FromTeamsBotNode</c>). So a validated Teams message from an enrolled
///         user resolves Strong on both dimensions, which is the AB#5124 goal.
///     </para>
/// </remarks>
internal sealed class EntraIdVerifiedCallerDirectory(
    IEntraIdUserLookup lookup,
    ILogger<EntraIdVerifiedCallerDirectory> logger) : IVerifiedCallerDirectory
{
    public async Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default)
    {
        // Own only the EntraID object id. Every other kind is deliberately unresolved here — the
        // fail-closed answer while its per-channel WI is not wired (AB#5123/5125).
        if (sender.Kind != ChannelIdentifierKind.EntraIdObjectId)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sender.Value))
        {
            return null;
        }

        var record = await lookup.FindByObjectIdAsync(tenantId, sender.Value, cancellationToken);
        if (record == null)
        {
            // No EntraID IdP / no binding / no user: unresolved. The AB#5126 binder applies the
            // trigger's anonymous-mode decision (reject or run as the service account).
            logger.LogDebug(
                "[{TenantId}] EntraID object id could not be resolved to an OctoMesh user; sender is unresolved",
                tenantId);
            return null;
        }

        // effective = min(enrollment, message) — a binding is only as trustworthy as its weaker
        // dimension (AB#5122). Both are Strong for a validated Teams message from an IdP-enrolled user.
        var effectiveTrust = Min(record.EnrollmentTrust, sender.MessageTrust);

        var principal = new VerifiedPrincipal(
            record.SubjectId,
            record.HomeTenantId,
            record.Email,
            record.Name,
            record.Roles);

        logger.LogDebug(
            "[{TenantId}] Resolved EntraID sender to caller '{SubjectId}' with {RoleCount} role(s) (enrollment {Enrollment}, message {Message} ⇒ effective {Effective})",
            tenantId, principal.SubjectId, principal.Roles.Count, record.EnrollmentTrust,
            sender.MessageTrust, effectiveTrust);

        return new ResolvedCaller(principal, effectiveTrust);
    }

    /// <summary>The directory's effective-trust rule: a binding is only as strong as its weaker dimension.</summary>
    private static CallerTrustLevel Min(CallerTrustLevel enrollmentTrust, CallerTrustLevel messageTrust)
        => (int)enrollmentTrust <= (int)messageTrust ? enrollmentTrust : messageTrust;
}

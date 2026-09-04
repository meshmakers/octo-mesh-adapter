using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The verified-caller directory for the phone / Signal path (AB#5123): it resolves a phone
///     sender's number (<see cref="ChannelIdentifierKind.PhoneNumber" />) to the OctoMesh user that
///     self-service phone enrollment bound to it (source = SelfService, OTP-proven Strong), through
///     the AB#5122 verified-identifier directory. The phone counterpart of
///     <see cref="EntraIdVerifiedCallerDirectory" /> (AB#5124), and the leaf the
///     <see cref="CompositeVerifiedCallerDirectory" /> dispatches phone senders to. <c>FromSignalNode</c>
///     already produces a phone <see cref="ChannelSender" />.
/// </summary>
/// <remarks>
///     <b>Trust.</b> The effective trust is <c>min(enrollmentTrust, messageTrust)</c> — the same rule
///     AB#5122's resolver applies. The enrollment dimension is stored on the binding (self-service OTP
///     writes it Strong); the message dimension is what the Signal trigger vouched for THIS message
///     (Strong only when the Signal protocol authenticated the sender). So a Signal-verified message
///     from an OTP-enrolled number resolves Strong on both dimensions — the AB#5123 goal.
/// </remarks>
internal sealed class PhoneVerifiedCallerDirectory(
    IPhoneUserLookup lookup,
    ILogger<PhoneVerifiedCallerDirectory> logger) : IKindVerifiedCallerDirectory
{
    public bool Owns(ChannelIdentifierKind kind) => kind == ChannelIdentifierKind.PhoneNumber;

    public async Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default)
    {
        // Own only the phone number. Every other kind is deliberately unresolved here — the composite
        // routes them to their own leaf (EntraID / e-mail).
        if (!Owns(sender.Kind))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sender.Value))
        {
            return null;
        }

        var record = await lookup.FindByPhoneNumberAsync(tenantId, sender.Value, cancellationToken);
        if (record == null)
        {
            // No verified-identifier directory / no binding / no user: unresolved. The AB#5126 binder
            // applies the trigger's anonymous-mode decision (reject or run as the service account).
            logger.LogDebug(
                "[{TenantId}] Phone number could not be resolved to an OctoMesh user; sender is unresolved",
                tenantId);
            return null;
        }

        // effective = min(enrollment, message) — a binding is only as trustworthy as its weaker
        // dimension (AB#5122). Both are Strong for a Signal-verified message from an OTP-enrolled number.
        var effectiveTrust = Min(record.EnrollmentTrust, sender.MessageTrust);

        var principal = new VerifiedPrincipal(
            record.SubjectId,
            record.HomeTenantId,
            record.Email,
            record.Name,
            record.Roles);

        logger.LogDebug(
            "[{TenantId}] Resolved phone sender to caller '{SubjectId}' with {RoleCount} role(s) (enrollment {Enrollment}, message {Message} ⇒ effective {Effective})",
            tenantId, principal.SubjectId, principal.Roles.Count, record.EnrollmentTrust,
            sender.MessageTrust, effectiveTrust);

        return new ResolvedCaller(principal, effectiveTrust);
    }

    /// <summary>The directory's effective-trust rule: a binding is only as strong as its weaker dimension.</summary>
    private static CallerTrustLevel Min(CallerTrustLevel enrollmentTrust, CallerTrustLevel messageTrust)
        => (int)enrollmentTrust <= (int)messageTrust ? enrollmentTrust : messageTrust;
}

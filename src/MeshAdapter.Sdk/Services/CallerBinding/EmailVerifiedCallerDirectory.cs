using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The verified-caller directory for the e-mail path (AB#5125): it resolves an inbound mail
///     sender's <see cref="ChannelIdentifierKind.EmailAddress" /> to the OctoMesh user an admin bound
///     it to (source = Admin, verified whitelist, enrollment Strong) — or, later, a self-service
///     e-mail OTP enrollment — through the AB#5122 verified-identifier directory. The e-mail
///     counterpart of <see cref="PhoneVerifiedCallerDirectory" /> (AB#5123) and
///     <see cref="EntraIdVerifiedCallerDirectory" /> (AB#5124), and the leaf the
///     <see cref="CompositeVerifiedCallerDirectory" /> dispatches e-mail senders to.
///     <c>FromEmailNode</c> (IMAP) and <c>FromMicrosoftGraphEmailNode</c> (Graph) already produce an
///     e-mail <see cref="ChannelSender" />.
/// </summary>
/// <remarks>
///     <b>Trust — the e-mail subtlety.</b> The effective trust is <c>min(enrollmentTrust, messageTrust)</c>,
///     the same rule AB#5122's resolver applies. The enrollment dimension is stored on the binding
///     (an admin whitelist writes it Strong); the message dimension is what the e-mail trigger vouched
///     for THIS message, derived from the <c>Authentication-Results</c> (DKIM/DMARC) verdict — Strong
///     only when the receiving server reported <c>dkim=pass</c> and an aligned <c>dmarc=pass</c>,
///     otherwise Weak. So even a strongly-enrolled address resolves only <b>Weak</b> for a message
///     that carries no valid DKIM/DMARC — an SMTP From is otherwise spoofable — and a weak (no-DKIM)
///     e-mail binding can never authorize an elevated operation. Both dimensions are Strong only for a
///     DKIM/DMARC-authenticated mail from an admin-enrolled (or OTP-enrolled) address.
/// </remarks>
internal sealed class EmailVerifiedCallerDirectory(
    IEmailUserLookup lookup,
    ILogger<EmailVerifiedCallerDirectory> logger) : IKindVerifiedCallerDirectory
{
    public bool Owns(ChannelIdentifierKind kind) => kind == ChannelIdentifierKind.EmailAddress;

    public async Task<ResolvedCaller?> ResolveAsync(string tenantId, ChannelSender sender,
        CancellationToken cancellationToken = default)
    {
        // Own only the e-mail address. Every other kind is deliberately unresolved here — the
        // composite routes them to their own leaf (phone / EntraID).
        if (!Owns(sender.Kind))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sender.Value))
        {
            return null;
        }

        var record = await lookup.FindByEmailAddressAsync(tenantId, sender.Value, cancellationToken);
        if (record == null)
        {
            // No verified-identifier directory / no binding / no user: unresolved. The AB#5126 binder
            // applies the trigger's anonymous-mode decision (reject or run as the service account).
            logger.LogDebug(
                "[{TenantId}] E-mail address could not be resolved to an OctoMesh user; sender is unresolved",
                tenantId);
            return null;
        }

        // effective = min(enrollment, message) — a binding is only as trustworthy as its weaker
        // dimension (AB#5122). The message dimension is the DKIM/DMARC verdict the trigger derived,
        // so a strongly-enrolled address on a spoofable (no-DKIM) mail stays Weak.
        var effectiveTrust = Min(record.EnrollmentTrust, sender.MessageTrust);

        var principal = new VerifiedPrincipal(
            record.SubjectId,
            record.HomeTenantId,
            record.Email,
            record.Name,
            record.Roles);

        logger.LogDebug(
            "[{TenantId}] Resolved e-mail sender to caller '{SubjectId}' with {RoleCount} role(s) (enrollment {Enrollment}, message {Message} ⇒ effective {Effective})",
            tenantId, principal.SubjectId, principal.Roles.Count, record.EnrollmentTrust,
            sender.MessageTrust, effectiveTrust);

        return new ResolvedCaller(principal, effectiveTrust);
    }

    /// <summary>The directory's effective-trust rule: a binding is only as strong as its weaker dimension.</summary>
    private static CallerTrustLevel Min(CallerTrustLevel enrollmentTrust, CallerTrustLevel messageTrust)
        => (int)enrollmentTrust <= (int)messageTrust ? enrollmentTrust : messageTrust;
}

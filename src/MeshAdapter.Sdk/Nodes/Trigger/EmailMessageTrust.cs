using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     Derives the per-message trust (<see cref="CallerTrustLevel" />) of an inbound e-mail from the
///     receiving mail server's <c>Authentication-Results</c> (DKIM/DMARC) verdict (AB#5125). This is
///     the MESSAGE dimension the e-mail <c>ChannelSender</c> carries; the verified-caller directory
///     combines it with the binding's stored ENROLLMENT dimension as <c>min(enrollment, message)</c>,
///     so this value caps how far an admin-enrolled address may be trusted for a given message.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why the message dimension exists at all.</b> An SMTP <c>From</c> is trivially
///         spoofable — the only evidence a mail was really sent by the claimed address is what the
///         RECEIVING infrastructure decided and stamped in <c>Authentication-Results</c>. So even a
///         strongly-enrolled (admin-whitelisted) address proves nothing per message unless that
///         message is DKIM/DMARC-authenticated.
///     </para>
///     <para>
///         <b>The rule.</b> <c>Strong</c> only when the server reported <c>dkim=pass</c> AND an aligned
///         <c>dmarc=pass</c> — DMARC is the check that ties the authenticated domain to the visible
///         <c>From</c>, so <see cref="EmailAuthenticationResults.IsDmarcPass" /> (a single, non-forged
///         header with <c>dmarc=pass</c>) plus a <see cref="EmailAuthenticationResults.HeaderFrom" />
///         that aligns with the address the pipeline actually reads. Everything else is
///         <c>Weak</c>: an absent header (the server did not stamp one — fail-safe, never silently
///         Strong), a <c>dmarc=fail</c>/<c>none</c>, a second sender-controlled header, or a
///         <c>From</c> the verdict does not cover. A weak e-mail binding must never authorize an
///         elevated operation, and <c>min(enrollment, Weak) ≤ Weak</c> guarantees exactly that.
///     </para>
/// </remarks>
internal static class EmailMessageTrust
{
    /// <summary>
    ///     Maps <paramref name="authentication" /> (parsed from the message's
    ///     <c>Authentication-Results</c> header, or <c>null</c> when none was present/fetched) and the
    ///     <paramref name="fromAddress" /> the pipeline reads onto the per-message
    ///     <see cref="CallerTrustLevel" />.
    /// </summary>
    internal static CallerTrustLevel Evaluate(EmailAuthenticationResults? authentication, string? fromAddress)
    {
        // No verdict known (header absent, or the trigger did not fetch headers): fail-safe to Weak —
        // an unauthenticated e-mail is spoofable and must never reach Strong silently.
        if (authentication is null)
        {
            return CallerTrustLevel.Weak;
        }

        // dmarc=pass from a single (non-forged) Authentication-Results header. IsDmarcPass already
        // rejects a second, sender-controlled header.
        if (!authentication.IsDmarcPass)
        {
            return CallerTrustLevel.Weak;
        }

        // dkim=pass as well: the WI gates Strong on BOTH DKIM and DMARC passing.
        if (!string.Equals(authentication.Dkim, "pass", StringComparison.Ordinal))
        {
            return CallerTrustLevel.Weak;
        }

        // From-alignment: the DMARC-evaluated header.from domain must be the domain of the address
        // the pipeline acts on. A dmarc=pass evaluated for a different From is not about this sender.
        if (!FromAligns(authentication.HeaderFrom, fromAddress))
        {
            return CallerTrustLevel.Weak;
        }

        return CallerTrustLevel.Strong;
    }

    /// <summary>
    ///     The message-trust of a batch is the WEAKEST of its messages' trust (fail-safe): a batch is
    ///     bound to a single sender, and one unauthenticated message in it means the sender identity
    ///     cannot be trusted Strong for the batch. An empty batch is <see cref="CallerTrustLevel.Weak" />.
    /// </summary>
    internal static CallerTrustLevel Min(IEnumerable<CallerTrustLevel> trusts)
    {
        var min = CallerTrustLevel.Strong;
        var any = false;
        foreach (var trust in trusts)
        {
            any = true;
            if ((int)trust < (int)min)
            {
                min = trust;
            }
        }

        return any ? min : CallerTrustLevel.Weak;
    }

    /// <summary>
    ///     Whether the DMARC <paramref name="headerFrom" /> domain aligns with the domain of
    ///     <paramref name="fromAddress" />. Both must be present; comparison is case-insensitive.
    /// </summary>
    private static bool FromAligns(string? headerFrom, string? fromAddress)
    {
        if (string.IsNullOrWhiteSpace(headerFrom) || string.IsNullOrWhiteSpace(fromAddress))
        {
            return false;
        }

        var domain = DomainOf(fromAddress);
        return domain.Length > 0 &&
               string.Equals(domain, headerFrom.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the domain part of an e-mail address (after the last '@'), tolerant of angle brackets.</summary>
    private static string DomainOf(string address)
    {
        var cleaned = address.Trim().TrimEnd('>').Trim();
        var at = cleaned.LastIndexOf('@');
        if (at < 0 || at == cleaned.Length - 1)
        {
            return string.Empty;
        }

        return cleaned[(at + 1)..].Trim().TrimEnd('.');
    }
}

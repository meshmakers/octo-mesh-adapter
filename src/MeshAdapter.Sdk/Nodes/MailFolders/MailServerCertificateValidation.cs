using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     Server-certificate policy for the MailKit clients of this adapter (<c>FromEmail@1</c>,
///     <c>ListMailFolders@1</c>): a chain that is trusted, matches the host and is unexpired is
///     accepted even when its <b>revocation status could not be determined</b>; every other defect
///     is refused as before.
///     <para>
///     MailKit's default validation hard-fails on <see cref="X509ChainStatusFlags.RevocationStatusUnknown" />.
///     On a developer Mac that is the ONLY chain status .NET reports for <c>imap.gmail.com</c> —
///     the platform cannot complete the online CRL/OCSP lookup for Google's chain — so the IMAP
///     channel and its folder picker refused a perfectly valid mailbox with "an incomplete
///     certificate revocation check occurred" (found 4.10.2026 through the AB#5370 picker, which
///     is the first place the handshake error ever reached an operator). Browsers and every
///     mainstream mail client soft-fail exactly this case: an unreachable revocation service is
///     not evidence of a revoked certificate. A revoked certificate (<c>Revoked</c>), an untrusted
///     chain, a name mismatch or an expired certificate are still refused — the soft-fail covers
///     revocation <i>availability</i> only, never a revocation <i>verdict</i>.
///     </para>
/// </summary>
internal static class MailServerCertificateValidation
{
    /// <summary>The flags that mean "the revocation service could not be consulted".</summary>
    private const X509ChainStatusFlags RevocationUnavailable =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>Installs the policy on <paramref name="client" /> (an IMAP, POP3 or SMTP client of MailKit).</summary>
    internal static void Apply(MailService client, ILogger? logger)
    {
        client.ServerCertificateValidationCallback =
            (_, certificate, chain, errors) => Validate(certificate, chain, errors, logger);
    }

    /// <summary>
    ///     The <see cref="RemoteCertificateValidationCallback" /> body: true for a clean chain, true
    ///     with a logged warning when the chain's only complaint is an unavailable revocation
    ///     service, false for everything else.
    /// </summary>
    internal static bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors,
        ILogger? logger)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (!IsOnlyUnavailableRevocation(errors, chain?.ChainStatus.Select(s => s.Status)))
        {
            return false;
        }

        logger?.LogWarning(
            "Mail server certificate '{Subject}' accepted although its revocation status could not be " +
            "determined (the CRL/OCSP service was not reachable from this host); the chain is otherwise valid.",
            certificate?.Subject ?? "<unknown>");
        return true;
    }

    /// <summary>
    ///     True when the policy errors consist of chain errors ONLY and every reported chain status
    ///     is one of the "revocation unavailable" flags — nothing else may be set on any element.
    ///     An empty status list with a chain error is NOT accepted: it means the error came from
    ///     somewhere this policy cannot see.
    /// </summary>
    internal static bool IsOnlyUnavailableRevocation(SslPolicyErrors errors,
        IEnumerable<X509ChainStatusFlags>? chainStatus)
    {
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors)
        {
            return false;
        }

        var statuses = chainStatus?.ToList();
        if (statuses is not { Count: > 0 })
        {
            return false;
        }

        return statuses.All(status =>
            status != X509ChainStatusFlags.NoError &&
            (status & ~RevocationUnavailable) == X509ChainStatusFlags.NoError);
    }
}

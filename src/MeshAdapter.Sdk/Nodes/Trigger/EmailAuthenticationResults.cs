namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     The sender-authentication verdicts a receiving mail server recorded in the
///     <c>Authentication-Results</c> header (RFC 8601): SPF, DKIM, DMARC and — on Microsoft 365 —
///     <c>compauth</c>. AB#5011.
/// </summary>
/// <remarks>
///     <para>
///         An ingest pipeline that acts on the sender address (a sender gate, an "invoices from this
///         vendor" rule, anything that turns a mail into a document) is trusting a header field that
///         anybody can write. These verdicts are the only part of a mail that says whether the
///         claimed sender really sent it, and they are produced by the receiving infrastructure, not
///         by the sender.
///     </para>
///     <para>
///         🔴 <b>DMARC is the one to gate on.</b> SPF authenticates the envelope sender
///         (<c>smtp.mailfrom</c>) and DKIM the signing domain (<c>header.d</c>); neither has to match
///         the <c>From:</c> address the pipeline actually reads. DMARC is the check that requires that
///         alignment, so <c>dmarc=pass</c> is the verdict that means "the visible From address is
///         genuine". A rule written against <c>spf=pass</c> alone accepts a mail whose envelope sender
///         is the attacker's own (perfectly SPF-valid) domain while <c>From:</c> claims the vendor's.
///     </para>
///     <para>
///         Values are the raw result keywords, lower-cased, exactly as the header spelled them —
///         <c>pass</c>, <c>fail</c>, <c>softfail</c>, <c>neutral</c>, <c>none</c>, <c>temperror</c>,
///         <c>permerror</c>, <c>bestguesspass</c>, … — and <c>null</c> when the method was not
///         reported at all. They are deliberately not mapped onto a boolean or an enum: "not reported"
///         and "reported as none" are different facts, an enum would have to guess at the vendor
///         extensions, and a pipeline comparing strings can be read by the person who has to audit it.
///     </para>
/// </remarks>
/// <param name="Spf">The <c>spf=</c> result, or null when not reported.</param>
/// <param name="Dkim">The <c>dkim=</c> result, or null when not reported.</param>
/// <param name="Dmarc">The <c>dmarc=</c> result, or null when not reported.</param>
/// <param name="CompAuth">
///     Microsoft's composite authentication verdict (<c>compauth=</c>), or null. Present only on
///     Exchange Online and worth reading there: it is the verdict Microsoft itself acts on.
/// </param>
/// <param name="SmtpMailFrom">The <c>smtp.mailfrom</c> property SPF authenticated, if reported.</param>
/// <param name="DkimDomain">The <c>header.d</c> property DKIM signed for, if reported.</param>
/// <param name="HeaderFrom">
///     The <c>header.from</c> domain DMARC evaluated. Compare it with the domain of
///     <see cref="EmailData.FromAddress" /> before trusting a <c>dmarc=pass</c> — they normally agree,
///     and a disagreement means the verdict is not about the address the pipeline is reading.
/// </param>
/// <param name="HeaderCount">
///     How many <c>Authentication-Results</c> headers the message carried. 🔴 Anything above 1 is a
///     reason to be suspicious: a sender can put such a header into the message they submit, and the
///     receiving server prepends its own rather than replacing it. Only the FIRST one — the one this
///     record was parsed from — was written by the receiving infrastructure; the rest are
///     sender-controlled text.
/// </param>
/// <param name="Raw">The unparsed first header value, so a pipeline can inspect what is not modelled.</param>
public sealed record EmailAuthenticationResults(
    string? Spf,
    string? Dkim,
    string? Dmarc,
    string? CompAuth,
    string? SmtpMailFrom,
    string? DkimDomain,
    string? HeaderFrom,
    int HeaderCount,
    string? Raw)
{
    /// <summary>
    ///     Convenience for the common gate: DMARC reported <c>pass</c>, and no second
    ///     (sender-controlled) <c>Authentication-Results</c> header was present to muddy it.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrow. It is not "the mail is safe" and it is not a substitute for comparing
    ///     <see cref="HeaderFrom" /> with the address the pipeline acts on — it is only the one
    ///     verdict a rule may safely be built on top of, expressed once so that every pipeline does
    ///     not re-derive it. Note that <c>MatchRegEx</c> in this platform is case sensitive, which is
    ///     exactly the kind of detail that makes a hand-written header check quietly accept
    ///     everything.
    /// </remarks>
    public bool IsDmarcPass => HeaderCount == 1
                               && string.Equals(Dmarc, "pass", StringComparison.Ordinal);
}

/// <summary>
///     Parses the RFC 8601 <c>Authentication-Results</c> header. Pure and static so the parsing is
///     unit-testable without a mailbox. AB#5011.
/// </summary>
internal static class AuthenticationResultsParser
{
    /// <summary>The header carrying the verdicts, as the receiving server writes it.</summary>
    internal const string HeaderName = "Authentication-Results";

    /// <summary>
    ///     Parses <paramref name="headerValue" /> into <see cref="EmailAuthenticationResults" />.
    /// </summary>
    /// <param name="headerValue">
    ///     The <b>first</b> <c>Authentication-Results</c> header of the message — the one the receiving
    ///     infrastructure prepended. Never a later one, and never several joined together: a sender
    ///     can put such a header into the message they submit, and joining would let their
    ///     <c>dmarc=pass</c> be found by a downstream substring check.
    /// </param>
    /// <param name="headerCount">How many such headers the message carried.</param>
    /// <returns>The parsed verdicts; every field is null when the header says nothing about it.</returns>
    internal static EmailAuthenticationResults Parse(string? headerValue, int headerCount)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return new EmailAuthenticationResults(null, null, null, null, null, null, null, headerCount,
                headerValue);
        }

        string? spf = null, dkim = null, dmarc = null, compAuth = null;
        string? smtpMailFrom = null, dkimDomain = null, headerFrom = null;

        // Comments are stripped from the WHOLE value before anything is split. RFC 5322 comments are
        // free text and routinely contain the very characters the structure is made of — Exchange
        // writes "(client-ip=1.2.3.4; helo=mail.example)" — so splitting first tears an entry in two
        // and turns the comment's own "key=value" pairs into method results.
        var withoutComments = RemoveComments(headerValue);

        // The header is a ';'-separated list of "method=result" entries, each optionally followed by
        // property assignments (smtp.mailfrom=…, header.d=…). The FIRST entry is usually the
        // authserv-id — the name of the server that made the judgement, with no '=' at all — and is
        // simply skipped by the "does this token contain '='" test below.
        foreach (var part in withoutComments.Split(';'))
        {
            var cleaned = part.Trim();
            if (cleaned.Length == 0)
            {
                continue;
            }

            // Whitespace separates the method result from its properties; both are "name=value".
            var tokens = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string? method = null;

            foreach (var token in tokens)
            {
                var separator = token.IndexOf('=');
                if (separator <= 0 || separator == token.Length - 1)
                {
                    continue;
                }

                var name = token[..separator].Trim().ToLowerInvariant();
                var value = Unquote(token[(separator + 1)..].Trim());

                if (method == null)
                {
                    // First name=value of the entry: the method and its result.
                    method = name;
                    switch (name)
                    {
                        case "spf":
                            spf ??= value.ToLowerInvariant();
                            break;
                        case "dkim":
                            // Several DKIM signatures produce several dkim= entries. The first one is
                            // kept: overwriting would let a failing second signature mask a passing
                            // first, and merging would need a policy this parser must not invent.
                            dkim ??= value.ToLowerInvariant();
                            break;
                        case "dmarc":
                            dmarc ??= value.ToLowerInvariant();
                            break;
                        case "compauth":
                            compAuth ??= value.ToLowerInvariant();
                            break;
                    }

                    continue;
                }

                // Property of the entry just read — only kept for the method it belongs to, so a
                // header.d from a DKIM entry can never be reported as the DMARC header.from.
                switch (method)
                {
                    case "spf" when name == "smtp.mailfrom":
                        smtpMailFrom ??= value;
                        break;
                    case "dkim" when name == "header.d":
                        dkimDomain ??= value;
                        break;
                    case "dmarc" when name == "header.from":
                        headerFrom ??= value;
                        break;
                }
            }
        }

        return new EmailAuthenticationResults(spf, dkim, dmarc, compAuth, smtpMailFrom, dkimDomain,
            headerFrom, headerCount, headerValue);
    }

    /// <summary>
    ///     Strips RFC 5322 comments — <c>(sender IP is 1.2.3.4)</c>,
    ///     <c>(client-ip=1.2.3.4; helo=mail.example)</c> — from the whole header value. They are free
    ///     text and regularly contain both ';' and '=', so they have to go <b>before</b> the value is
    ///     split into entries. Nesting is legal and is handled by counting depth.
    /// </summary>
    private static string RemoveComments(string value)
    {
        if (!value.Contains('(', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var depth = 0;
        foreach (var c in value)
        {
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    // A comment sits between tokens; replacing it with a space keeps them apart.
                    builder.Append(' ');
                    break;
                default:
                    if (depth == 0)
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }
}

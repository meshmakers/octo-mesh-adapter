using System.Text.Json;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
///     AB#5011: the e-mail ingest can only check a sender's authenticity if the trigger exposes what
///     the receiving mail server decided — the <c>Authentication-Results</c> header. Covers the
///     Graph <c>$select</c> that makes the header available at all, the header-surfacing rules, and
///     the RFC 8601 parser.
/// </summary>
public class FromMicrosoftGraphEmailNodeAuthenticationResultsTests
{
    // A real Exchange Online header, reformatted onto one line.
    private const string MicrosoftHeader =
        "spf.protection.outlook.com; spf=pass (sender IP is 40.107.20.55) smtp.mailfrom=vendor.example; "
        + "dkim=pass (signature was verified) header.d=vendor.example;dmarc=pass action=none "
        + "header.from=vendor.example;compauth=pass reason=100";

    // =============================================================================================
    // The query. Everything below is unreachable without this one word in the $select.
    // =============================================================================================

    [Fact]
    public void TheHeadersAreOnlyRequestedWhenTheNodeAsksForThem()
    {
        // Graph returns internetMessageHeaders ONLY when it is selected — the header was not empty
        // before this work item, it was never fetched. That also means the flag is genuinely inert:
        // an existing pipeline gets the byte-identical query it got before.
        Assert.DoesNotContain("internetMessageHeaders",
            FromMicrosoftGraphEmailNode.BuildMessageSelect(includeInternetMessageHeaders: false),
            StringComparison.Ordinal);

        Assert.Contains("internetMessageHeaders",
            FromMicrosoftGraphEmailNode.BuildMessageSelect(includeInternetMessageHeaders: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheExistingSelectionIsUnchangedByTheAddition()
    {
        var select = FromMicrosoftGraphEmailNode.BuildMessageSelect(includeInternetMessageHeaders: true);

        foreach (var property in new[]
                 {
                     "id", "subject", "from", "toRecipients", "receivedDateTime", "body", "hasAttachments",
                     "internetMessageId"
                 })
        {
            Assert.Contains(property, select.Split(','), StringComparer.Ordinal);
        }
    }

    // =============================================================================================
    // Surfacing the headers onto EmailData.
    // =============================================================================================

    [Fact]
    public void TheAuthenticationVerdictsReachTheEmailData()
    {
        var emailData = ApplyHeaders(("Authentication-Results", MicrosoftHeader));

        Assert.NotNull(emailData.Authentication);
        Assert.Equal("pass", emailData.Authentication!.Spf);
        Assert.Equal("pass", emailData.Authentication.Dkim);
        Assert.Equal("pass", emailData.Authentication.Dmarc);
        Assert.True(emailData.Authentication.IsDmarcPass);
        Assert.Equal(MicrosoftHeader, emailData.Headers["Authentication-Results"]);
    }

    [Fact]
    public void AMessageWithoutInternetHeadersLeavesTheAuthenticationUnknown()
    {
        // 🔴 Null means "nothing is known", NOT "authentication failed". Graph omits the property for
        // an internally generated or draft mail, and a gate has to decide explicitly what to do with
        // an unknown verdict — which way that falls is tenant policy, not the trigger's call.
        var message = JsonDocument.Parse("""{ "id": "AAA" }""").RootElement;
        var emailData = new EmailData();

        FromMicrosoftGraphEmailNode.ApplyInternetMessageHeaders(emailData, message, configuredNames: null);

        Assert.Null(emailData.Authentication);
        Assert.Empty(emailData.Headers);
    }

    [Fact]
    public void AMessageWithHeadersButNoAuthenticationResultsLeavesTheVerdictsUnknown()
    {
        var emailData = ApplyHeaders(("Received-SPF", "Pass (protection.outlook.com: domain of vendor.example)"));

        Assert.Null(emailData.Authentication);
        Assert.True(emailData.Headers.ContainsKey("Received-SPF"));
    }

    [Fact]
    public void OnlyTheFirstOccurrenceOfAHeaderIsKept_AndTheDuplicateIsReported()
    {
        // 🔴 The attack this rule exists for. A sender can put an Authentication-Results header into
        // the message they submit; the receiving server PREPENDS its own rather than replacing it, so
        // only the first one is trustworthy. Joining them — the obvious way to "not lose data" —
        // would put the forged dmarc=pass into the same string as the real dmarc=fail, where any
        // downstream substring or regex check finds it.
        var emailData = ApplyHeaders(
            ("Authentication-Results", "mx.example; spf=fail smtp.mailfrom=attacker.example;dmarc=fail"),
            ("Authentication-Results", "forged.example; spf=pass;dkim=pass;dmarc=pass"));

        Assert.NotNull(emailData.Authentication);
        Assert.Equal("fail", emailData.Authentication!.Dmarc);
        Assert.Equal(2, emailData.Authentication.HeaderCount);
        Assert.DoesNotContain("pass", emailData.Headers["Authentication-Results"], StringComparison.Ordinal);

        // And the convenience gate refuses a message carrying a second, sender-controlled header even
        // if the trustworthy one had passed.
        Assert.False(emailData.Authentication.IsDmarcPass);
    }

    [Fact]
    public void ASecondAuthenticationResultsHeaderDefeatsIsDmarcPassEvenWhenTheRealOnePassed()
    {
        var emailData = ApplyHeaders(
            ("Authentication-Results", "mx.example; dmarc=pass header.from=vendor.example"),
            ("Authentication-Results", "forged.example; dmarc=pass"));

        Assert.Equal("pass", emailData.Authentication!.Dmarc);
        Assert.False(emailData.Authentication.IsDmarcPass);
    }

    [Fact]
    public void HeadersOutsideTheConfiguredSetAreNotCarriedIntoThePipelineData()
    {
        // The headers land in the data context, which is echoed into every debug view and persisted
        // by SetPipelineExecutionResult@1. The full set is kilobytes of Received chain per message.
        var emailData = ApplyHeaders(
            new[] { "Authentication-Results" },
            ("Authentication-Results", MicrosoftHeader),
            ("Received", "from mx.example by outlook.com; Mon, 1 Sep 2026 08:00:00 +0000"),
            ("DKIM-Signature", "v=1; a=rsa-sha256; d=vendor.example; b=" + new string('A', 400)));

        Assert.Single(emailData.Headers);
        Assert.True(emailData.Headers.ContainsKey("Authentication-Results"));
    }

    [Fact]
    public void AConfiguredSetThatOmitsAuthenticationResultsStillGetsIt()
    {
        // A name list that dropped it would turn the verdicts off silently while the flag says they
        // are on — the failure mode that looks like "nothing happened" rather than like an error.
        var emailData = ApplyHeaders(
            new[] { "Received-SPF" },
            ("Authentication-Results", MicrosoftHeader),
            ("Received-SPF", "Pass"));

        Assert.NotNull(emailData.Authentication);
        Assert.Equal("pass", emailData.Authentication!.Dmarc);
        Assert.True(emailData.Headers.ContainsKey("Authentication-Results"));
    }

    [Fact]
    public void HeaderNamesAreMatchedCaseInsensitively()
    {
        // Header names are case insensitive per RFC 5322, and servers do differ ("ARC-Authentication-
        // Results" vs "arc-authentication-results"). A case-sensitive match would drop the verdicts
        // for whole mail platforms and report nothing at all.
        var emailData = ApplyHeaders(("authentication-results", MicrosoftHeader));

        Assert.NotNull(emailData.Authentication);
        Assert.Equal("pass", emailData.Authentication!.Dmarc);
        Assert.Equal(MicrosoftHeader, emailData.Headers["AUTHENTICATION-RESULTS"]);
    }

    // =============================================================================================
    // The parser (RFC 8601).
    // =============================================================================================

    [Fact]
    public void TheMicrosoftHeaderIsParsedIntoItsFourVerdictsAndTheirProperties()
    {
        var results = AuthenticationResultsParser.Parse(MicrosoftHeader, headerCount: 1);

        Assert.Equal("pass", results.Spf);
        Assert.Equal("pass", results.Dkim);
        Assert.Equal("pass", results.Dmarc);
        Assert.Equal("pass", results.CompAuth);
        Assert.Equal("vendor.example", results.SmtpMailFrom);
        Assert.Equal("vendor.example", results.DkimDomain);
        Assert.Equal("vendor.example", results.HeaderFrom);
        Assert.Equal(MicrosoftHeader, results.Raw);
    }

    [Fact]
    public void CommentsAreStrippedBeforeTheStructureIsRead()
    {
        // RFC 5322 comments are free text and routinely contain both ';' and '=' — "(sender IP is
        // 1.2.3.4)", "(signature was verified; key=2048)". Reading the structure first would split on
        // a semicolon inside a comment and read "key=2048" as a method result.
        var results = AuthenticationResultsParser.Parse(
            "mx.example; spf=pass (client-ip=1.2.3.4; helo=mail.vendor.example) smtp.mailfrom=vendor.example; "
            + "dmarc=fail (p=reject sp=reject dis=none) header.from=vendor.example",
            headerCount: 1);

        Assert.Equal("pass", results.Spf);
        Assert.Equal("vendor.example", results.SmtpMailFrom);
        Assert.Equal("fail", results.Dmarc);
        Assert.Equal("vendor.example", results.HeaderFrom);
    }

    [Fact]
    public void NestedCommentsAreStripped()
    {
        var results = AuthenticationResultsParser.Parse(
            "mx.example; dmarc=pass (policy (inherited) applied) header.from=vendor.example",
            headerCount: 1);

        Assert.Equal("pass", results.Dmarc);
        Assert.Equal("vendor.example", results.HeaderFrom);
    }

    [Fact]
    public void TheAuthservIdIsNotMistakenForAMethod()
    {
        // The first entry names the server that made the judgement and carries no '=' at all.
        var results = AuthenticationResultsParser.Parse("spf.protection.outlook.com; dkim=pass", headerCount: 1);

        Assert.Equal("pass", results.Dkim);
        Assert.Null(results.Spf);
        Assert.Null(results.Dmarc);
    }

    [Fact]
    public void AMethodThatWasNotReportedStaysNull()
    {
        // "not reported" and "reported as none" are different facts and must stay distinguishable —
        // mapping the absent method onto "none" would let a gate accept a server that never checked.
        var results = AuthenticationResultsParser.Parse("mx.example; spf=none", headerCount: 1);

        Assert.Equal("none", results.Spf);
        Assert.Null(results.Dkim);
        Assert.Null(results.Dmarc);
        Assert.Null(results.CompAuth);
    }

    [Fact]
    public void TheFirstOfSeveralDkimEntriesWins()
    {
        // A mail may be signed several times, producing several dkim= entries. Overwriting would let
        // a failing later signature mask a passing first one; merging would need a policy this parser
        // must not invent.
        var results = AuthenticationResultsParser.Parse(
            "mx.example; dkim=pass header.d=vendor.example; dkim=fail header.d=list.example",
            headerCount: 1);

        Assert.Equal("pass", results.Dkim);
        Assert.Equal("vendor.example", results.DkimDomain);
    }

    [Fact]
    public void APropertyIsOnlyReportedForTheMethodItBelongsTo()
    {
        // header.d belongs to DKIM and header.from to DMARC. Reading them positionally would report a
        // DKIM signing domain as the DMARC-aligned From domain — the exact confusion that makes a
        // spoofed From look verified.
        var results = AuthenticationResultsParser.Parse(
            "mx.example; dkim=pass header.d=mailer.example; dmarc=fail header.from=vendor.example",
            headerCount: 1);

        Assert.Equal("mailer.example", results.DkimDomain);
        Assert.Equal("vendor.example", results.HeaderFrom);
    }

    [Fact]
    public void QuotedPropertyValuesAreUnquoted()
    {
        var results = AuthenticationResultsParser.Parse(
            "mx.example; dmarc=pass header.from=\"vendor.example\"", headerCount: 1);

        Assert.Equal("vendor.example", results.HeaderFrom);
    }

    [Fact]
    public void ResultKeywordsAreNormalisedToLowerCase()
    {
        // Servers differ in spelling ("Pass", "PASS"). The platform's MatchRegEx is case sensitive,
        // so a pipeline comparing against "pass" would silently never match on some senders.
        var results = AuthenticationResultsParser.Parse("mx.example; SPF=Pass; DMARC=PASS", headerCount: 1);

        Assert.Equal("pass", results.Spf);
        Assert.Equal("pass", results.Dmarc);
    }

    [Fact]
    public void AnEmptyOrGarbledHeaderYieldsNoVerdictsRatherThanThrowing()
    {
        // A malformed header must never take the mailbox poll down — it would block every message in
        // the folder, not just this one.
        foreach (var value in new[] { "", "   ", ";;;", "=", "no structure at all" })
        {
            var results = AuthenticationResultsParser.Parse(value, headerCount: 1);

            Assert.Null(results.Spf);
            Assert.Null(results.Dkim);
            Assert.Null(results.Dmarc);
            Assert.False(results.IsDmarcPass);
        }
    }

    [Fact]
    public void IsDmarcPassIsTrueOnlyForAPassFromASingleHeader()
    {
        Assert.True(AuthenticationResultsParser.Parse("mx; dmarc=pass", 1).IsDmarcPass);
        Assert.False(AuthenticationResultsParser.Parse("mx; dmarc=fail", 1).IsDmarcPass);
        Assert.False(AuthenticationResultsParser.Parse("mx; dmarc=bestguesspass", 1).IsDmarcPass);
        Assert.False(AuthenticationResultsParser.Parse("mx; spf=pass;dkim=pass", 1).IsDmarcPass);
        Assert.False(AuthenticationResultsParser.Parse("mx; dmarc=pass", 2).IsDmarcPass);
    }

    private static EmailData ApplyHeaders(params (string Name, string Value)[] headers)
    {
        return ApplyHeaders(null, headers);
    }

    private static EmailData ApplyHeaders(string[]? configuredNames,
        params (string Name, string Value)[] headers)
    {
        var payload = new
        {
            id = "AAA",
            internetMessageHeaders = headers.Select(h => new { name = h.Name, value = h.Value }).ToArray()
        };

        var message = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        var emailData = new EmailData();

        FromMicrosoftGraphEmailNode.ApplyInternetMessageHeaders(emailData, message, configuredNames);

        return emailData;
    }
}

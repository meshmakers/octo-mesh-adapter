using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
///     AB#5125: the e-mail ingest turns the receiving server's <c>Authentication-Results</c> verdict
///     into the per-message trust the caller-binding acts on. Proves the mapping: a dkim=pass +
///     aligned dmarc=pass single header ⇒ Strong; a fail, an absent header, a second (forged) header,
///     or a From the verdict does not cover ⇒ Weak (fail-safe, never silently Strong).
/// </summary>
public class EmailMessageTrustTests
{
    private const string From = "vendor@example.com";

    // A real Exchange Online header (one line), dkim=pass + dmarc=pass, header.from=example.com.
    private const string PassHeader =
        "spf.protection.outlook.com; spf=pass smtp.mailfrom=example.com; "
        + "dkim=pass (signature was verified) header.d=example.com;dmarc=pass action=none header.from=example.com";

    private static EmailAuthenticationResults Parse(string header, int count = 1)
        => AuthenticationResultsParser.Parse(header, count);

    [Fact]
    public void Dkim_and_aligned_dmarc_pass_is_Strong()
    {
        Assert.Equal(CallerTrustLevel.Strong, EmailMessageTrust.Evaluate(Parse(PassHeader), From));
    }

    [Fact]
    public void Absent_authentication_is_Weak_failsafe()
    {
        // Header not present / not fetched: "nothing known" must fail safe to Weak, never Strong.
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Evaluate(null, From));
    }

    [Fact]
    public void Dmarc_fail_is_Weak()
    {
        Assert.Equal(CallerTrustLevel.Weak,
            EmailMessageTrust.Evaluate(Parse("mx.example; dkim=pass header.d=example.com; dmarc=fail header.from=example.com"), From));
    }

    [Fact]
    public void Dmarc_pass_without_dkim_pass_is_Weak()
    {
        // The WI gates Strong on BOTH dkim=pass AND dmarc=pass. DMARC passing via SPF alignment alone
        // (dkim not reported / failed) is deliberately not enough to reach Strong.
        Assert.Equal(CallerTrustLevel.Weak,
            EmailMessageTrust.Evaluate(Parse("mx.example; spf=pass smtp.mailfrom=example.com; dmarc=pass header.from=example.com"), From));
    }

    [Fact]
    public void A_second_sender_controlled_header_defeats_Strong_even_when_the_real_one_passed()
    {
        // Two Authentication-Results headers: only the first was written by the receiving server.
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Evaluate(Parse(PassHeader, count: 2), From));
    }

    [Fact]
    public void A_dmarc_pass_for_a_different_From_domain_is_Weak()
    {
        // header.from is a domain the verdict was about; if it is not the domain of the address the
        // pipeline reads, the pass is not about this sender.
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Evaluate(Parse(PassHeader), "attacker@evil.example"));
    }

    [Fact]
    public void From_alignment_is_case_insensitive()
    {
        Assert.Equal(CallerTrustLevel.Strong, EmailMessageTrust.Evaluate(Parse(PassHeader), "VENDOR@Example.COM"));
    }

    [Fact]
    public void Angle_bracketed_From_still_aligns()
    {
        Assert.Equal(CallerTrustLevel.Strong, EmailMessageTrust.Evaluate(Parse(PassHeader), "vendor@example.com>"));
    }

    [Fact]
    public void Missing_From_is_Weak_even_on_a_pass_header()
    {
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Evaluate(Parse(PassHeader), null));
    }

    [Fact]
    public void Batch_min_takes_the_weakest_message()
    {
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Min(
            [CallerTrustLevel.Strong, CallerTrustLevel.Strong, CallerTrustLevel.Weak]));
        Assert.Equal(CallerTrustLevel.Strong, EmailMessageTrust.Min(
            [CallerTrustLevel.Strong, CallerTrustLevel.Strong]));
    }

    [Fact]
    public void Batch_min_of_an_empty_batch_is_Weak()
    {
        Assert.Equal(CallerTrustLevel.Weak, EmailMessageTrust.Min([]));
    }
}

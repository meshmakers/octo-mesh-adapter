using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CallerBinding;

/// <summary>
///     Proves the e-mail verified-caller directory (AB#5125): it resolves an inbound mail sender's
///     From address to the OctoMesh user an admin whitelist bound to it, maps the user onto a
///     token-free principal, and combines the stored enrollment trust with the DKIM/DMARC-derived
///     message trust into <c>effective = min(enrollment, message)</c>. It owns only the e-mail kind
///     and stays fail-closed (unresolved) for everything else. 🔴 The security-critical case: an
///     admin-enrolled (enrollment Strong) address on a spoofable (no-DKIM, message Weak) mail resolves
///     only Weak — it can never authorize an elevated operation.
/// </summary>
public class EmailVerifiedCallerDirectoryTests
{
    private const string TenantId = "acme";
    private const string EmailAddress = "vendor@example.com";

    private readonly IEmailUserLookup _lookup = A.Fake<IEmailUserLookup>();
    private readonly EmailVerifiedCallerDirectory _directory;

    public EmailVerifiedCallerDirectoryTests()
    {
        _directory = new EmailVerifiedCallerDirectory(_lookup,
            A.Fake<ILogger<EmailVerifiedCallerDirectory>>());
    }

    private void LookupReturns(EmailCallerRecord? record)
        => A.CallTo(() => _lookup.FindByEmailAddressAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(record));

    private static EmailCallerRecord Record(CallerTrustLevel enrollmentTrust)
        => new("user-rt-1", TenantId, "u@example.com", "u", ["Reader", "Writer"], enrollmentTrust);

    private static ChannelSender EmailSender(CallerTrustLevel messageTrust)
        => new(ChannelIdentifierKind.EmailAddress, EmailAddress, messageTrust);

    [Fact]
    public void Owns_only_the_email_kind()
    {
        Assert.True(_directory.Owns(ChannelIdentifierKind.EmailAddress));
        Assert.False(_directory.Owns(ChannelIdentifierKind.PhoneNumber));
        Assert.False(_directory.Owns(ChannelIdentifierKind.EntraIdObjectId));
    }

    [Fact]
    public async Task Non_email_kind_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.PhoneNumber, "+436601234567", CallerTrustLevel.Strong));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByEmailAddressAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Blank_address_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.EmailAddress, "  ", CallerTrustLevel.Strong));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByEmailAddressAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Unknown_address_resolves_to_null()
    {
        LookupReturns(null);

        var result = await _directory.ResolveAsync(TenantId, EmailSender(CallerTrustLevel.Strong));

        Assert.Null(result);
    }

    [Fact]
    public async Task Resolved_caller_maps_the_user_onto_the_principal()
    {
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, EmailSender(CallerTrustLevel.Strong));

        Assert.NotNull(result);
        Assert.Equal("user-rt-1", result!.Principal.SubjectId);
        Assert.Equal(TenantId, result.Principal.TenantId);
        Assert.Equal("u@example.com", result.Principal.Email);
        Assert.Equal("u", result.Principal.Name);
        Assert.Equal(["Reader", "Writer"], result.Principal.Roles);
    }

    [Fact]
    public async Task Dkim_authenticated_mail_from_an_admin_enrolled_address_is_Strong_on_both_dimensions()
    {
        // The AB#5125 goal: admin-whitelisted (enrollment Strong) + DKIM/DMARC-valid message
        // (message Strong) ⇒ effective Strong.
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, EmailSender(CallerTrustLevel.Strong));

        Assert.NotNull(result);
        Assert.Equal(CallerTrustLevel.Strong, result!.EffectiveTrust);
    }

    [Fact]
    public async Task Admin_enrolled_address_on_a_no_dkim_mail_stays_Weak_and_cannot_elevate()
    {
        // 🔴 The whole point of the message dimension: a strongly-enrolled address proves nothing per
        // message without valid DKIM/DMARC (SMTP From is spoofable), so effective is capped at Weak.
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, EmailSender(CallerTrustLevel.Weak));

        Assert.NotNull(result);
        Assert.Equal(CallerTrustLevel.Weak, result!.EffectiveTrust);
        Assert.False(result.EffectiveTrust.IsAtLeast(CallerTrustLevel.Strong));
    }

    [Theory]
    [InlineData(CallerTrustLevel.Strong, CallerTrustLevel.Weak, CallerTrustLevel.Weak)]
    [InlineData(CallerTrustLevel.Weak, CallerTrustLevel.Strong, CallerTrustLevel.Weak)]
    [InlineData(CallerTrustLevel.Strong, CallerTrustLevel.None, CallerTrustLevel.None)]
    [InlineData(CallerTrustLevel.Strong, CallerTrustLevel.Strong, CallerTrustLevel.Strong)]
    public async Task Effective_trust_is_the_minimum_of_enrollment_and_message(
        CallerTrustLevel enrollment, CallerTrustLevel message, CallerTrustLevel expected)
    {
        LookupReturns(Record(enrollment));

        var result = await _directory.ResolveAsync(TenantId, EmailSender(message));

        Assert.NotNull(result);
        Assert.Equal(expected, result!.EffectiveTrust);
    }
}

using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CallerBinding;

/// <summary>
///     Proves the phone verified-caller directory (AB#5123): it resolves a phone sender's number to
///     the OctoMesh user self-service enrollment bound to it, maps the user onto a token-free
///     principal, and combines the stored enrollment trust with the message trust into
///     <c>effective = min(enrollment, message)</c>. It owns only the phone kind and stays fail-closed
///     (unresolved) for everything else.
/// </summary>
public class PhoneVerifiedCallerDirectoryTests
{
    private const string TenantId = "acme";
    private const string PhoneNumber = "+436601234567";

    private readonly IPhoneUserLookup _lookup = A.Fake<IPhoneUserLookup>();
    private readonly PhoneVerifiedCallerDirectory _directory;

    public PhoneVerifiedCallerDirectoryTests()
    {
        _directory = new PhoneVerifiedCallerDirectory(_lookup,
            A.Fake<ILogger<PhoneVerifiedCallerDirectory>>());
    }

    private void LookupReturns(PhoneCallerRecord? record)
        => A.CallTo(() => _lookup.FindByPhoneNumberAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(record));

    private static PhoneCallerRecord Record(CallerTrustLevel enrollmentTrust)
        => new("user-rt-1", TenantId, "u@example.com", "u", ["Reader", "Writer"], enrollmentTrust);

    private ChannelSender PhoneSender(CallerTrustLevel messageTrust)
        => new(ChannelIdentifierKind.PhoneNumber, PhoneNumber, messageTrust);

    [Fact]
    public void Owns_only_the_phone_kind()
    {
        Assert.True(_directory.Owns(ChannelIdentifierKind.PhoneNumber));
        Assert.False(_directory.Owns(ChannelIdentifierKind.EntraIdObjectId));
        Assert.False(_directory.Owns(ChannelIdentifierKind.EmailAddress));
    }

    [Fact]
    public async Task Non_phone_kind_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.EmailAddress, "u@example.com", CallerTrustLevel.Weak));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByPhoneNumberAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Blank_number_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.PhoneNumber, "  ", CallerTrustLevel.Strong));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByPhoneNumberAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Unknown_number_resolves_to_null()
    {
        LookupReturns(null);

        var result = await _directory.ResolveAsync(TenantId, PhoneSender(CallerTrustLevel.Strong));

        Assert.Null(result);
    }

    [Fact]
    public async Task Resolved_caller_maps_the_user_onto_the_principal()
    {
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, PhoneSender(CallerTrustLevel.Strong));

        Assert.NotNull(result);
        Assert.Equal("user-rt-1", result!.Principal.SubjectId);
        Assert.Equal(TenantId, result.Principal.TenantId);
        Assert.Equal("u@example.com", result.Principal.Email);
        Assert.Equal("u", result.Principal.Name);
        Assert.Equal(["Reader", "Writer"], result.Principal.Roles);
    }

    [Fact]
    public async Task Signal_verified_message_from_an_enrolled_number_is_Strong_on_both_dimensions()
    {
        // The AB#5123 goal: OTP-enrolled (enrollment Strong) + Signal-verified message (message Strong).
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, PhoneSender(CallerTrustLevel.Strong));

        Assert.NotNull(result);
        Assert.Equal(CallerTrustLevel.Strong, result!.EffectiveTrust);
    }

    [Theory]
    [InlineData(CallerTrustLevel.Strong, CallerTrustLevel.Weak, CallerTrustLevel.Weak)]
    [InlineData(CallerTrustLevel.Weak, CallerTrustLevel.Strong, CallerTrustLevel.Weak)]
    [InlineData(CallerTrustLevel.Strong, CallerTrustLevel.None, CallerTrustLevel.None)]
    [InlineData(CallerTrustLevel.Weak, CallerTrustLevel.Weak, CallerTrustLevel.Weak)]
    public async Task Effective_trust_is_the_minimum_of_enrollment_and_message(
        CallerTrustLevel enrollment, CallerTrustLevel message, CallerTrustLevel expected)
    {
        LookupReturns(Record(enrollment));

        var result = await _directory.ResolveAsync(TenantId, PhoneSender(message));

        Assert.NotNull(result);
        Assert.Equal(expected, result!.EffectiveTrust);
    }
}

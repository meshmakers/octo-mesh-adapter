using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CallerBinding;

/// <summary>
///     Proves the EntraID verified-caller directory (AB#5124): it resolves a Teams sender's AAD
///     object id to the OctoMesh user the EntraID IdP provisioned, maps the user onto a token-free
///     principal, and combines the stored enrollment trust with the message trust into
///     <c>effective = min(enrollment, message)</c>. It owns only the EntraID kind and stays
///     fail-closed (unresolved) for everything else.
/// </summary>
public class EntraIdVerifiedCallerDirectoryTests
{
    private const string TenantId = "acme";
    private const string ObjectId = "11111111-2222-3333-4444-555555555555";

    private readonly IEntraIdUserLookup _lookup = A.Fake<IEntraIdUserLookup>();
    private readonly EntraIdVerifiedCallerDirectory _directory;

    public EntraIdVerifiedCallerDirectoryTests()
    {
        _directory = new EntraIdVerifiedCallerDirectory(_lookup,
            A.Fake<ILogger<EntraIdVerifiedCallerDirectory>>());
    }

    private void LookupReturns(EntraIdCallerRecord? record)
        => A.CallTo(() => _lookup.FindByObjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(record));

    private static EntraIdCallerRecord Record(CallerTrustLevel enrollmentTrust)
        => new("user-rt-1", TenantId, "u@example.com", "u", ["Reader", "Writer"], enrollmentTrust);

    private ChannelSender EntraIdSender(CallerTrustLevel messageTrust)
        => new(ChannelIdentifierKind.EntraIdObjectId, ObjectId, messageTrust);

    [Fact]
    public async Task Non_EntraId_kind_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.EmailAddress, "u@example.com", CallerTrustLevel.Weak));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByObjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Blank_object_id_is_unresolved_and_never_hits_the_lookup()
    {
        var result = await _directory.ResolveAsync(TenantId,
            new ChannelSender(ChannelIdentifierKind.EntraIdObjectId, "  ", CallerTrustLevel.Strong));

        Assert.Null(result);
        A.CallTo(() => _lookup.FindByObjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Unknown_object_id_resolves_to_null()
    {
        LookupReturns(null);

        var result = await _directory.ResolveAsync(TenantId, EntraIdSender(CallerTrustLevel.Strong));

        Assert.Null(result);
    }

    [Fact]
    public async Task Resolved_caller_maps_the_user_onto_the_principal()
    {
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, EntraIdSender(CallerTrustLevel.Strong));

        Assert.NotNull(result);
        Assert.Equal("user-rt-1", result!.Principal.SubjectId);
        Assert.Equal(TenantId, result.Principal.TenantId);
        Assert.Equal("u@example.com", result.Principal.Email);
        Assert.Equal("u", result.Principal.Name);
        Assert.Equal(["Reader", "Writer"], result.Principal.Roles);
    }

    [Fact]
    public async Task Validated_message_from_an_enrolled_user_is_Strong_on_both_dimensions()
    {
        // The AB#5124 goal: IdP-enrolled (enrollment Strong) + validated Teams token (message Strong).
        LookupReturns(Record(CallerTrustLevel.Strong));

        var result = await _directory.ResolveAsync(TenantId, EntraIdSender(CallerTrustLevel.Strong));

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

        var result = await _directory.ResolveAsync(TenantId, EntraIdSender(message));

        Assert.NotNull(result);
        Assert.Equal(expected, result!.EffectiveTrust);
    }
}

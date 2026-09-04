using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CallerBinding;

/// <summary>
///     Proves the composite verified-caller directory (AB#5123) dispatches a sender by its kind to the
///     leaf that owns it, so the EntraID (AB#5124) and phone (AB#5123) directories coexist behind the
///     one directory the binder consumes: a sender only ever reaches the owning leaf, an unowned kind
///     resolves to null (fail-closed), and the first non-null resolution wins.
/// </summary>
public class CompositeVerifiedCallerDirectoryTests
{
    private const string TenantId = "acme";

    private static IKindVerifiedCallerDirectory Leaf(ChannelIdentifierKind owned, ResolvedCaller? resolves)
    {
        var leaf = A.Fake<IKindVerifiedCallerDirectory>();
        A.CallTo(() => leaf.Owns(A<ChannelIdentifierKind>._))
            .ReturnsLazily((ChannelIdentifierKind k) => k == owned);
        A.CallTo(() => leaf.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .Returns(Task.FromResult(resolves));
        return leaf;
    }

    private static ResolvedCaller Caller(string subjectId)
        => new(new VerifiedPrincipal(subjectId, TenantId, null, null, []), CallerTrustLevel.Strong);

    private static ChannelSender Sender(ChannelIdentifierKind kind)
        => new(kind, "value", CallerTrustLevel.Strong);

    [Fact]
    public async Task Dispatches_to_the_leaf_that_owns_the_kind_and_skips_the_others()
    {
        var phone = Leaf(ChannelIdentifierKind.PhoneNumber, Caller("phone-user"));
        var entra = Leaf(ChannelIdentifierKind.EntraIdObjectId, Caller("entra-user"));
        var composite = new CompositeVerifiedCallerDirectory([entra, phone],
            A.Fake<ILogger<CompositeVerifiedCallerDirectory>>());

        var result = await composite.ResolveAsync(TenantId, Sender(ChannelIdentifierKind.PhoneNumber));

        Assert.NotNull(result);
        Assert.Equal("phone-user", result!.Principal.SubjectId);
        // The non-owning (EntraID) leaf must never be queried for a phone sender.
        A.CallTo(() => entra.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => phone.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task A_kind_no_leaf_owns_resolves_to_null_and_queries_no_leaf()
    {
        var phone = Leaf(ChannelIdentifierKind.PhoneNumber, Caller("phone-user"));
        var entra = Leaf(ChannelIdentifierKind.EntraIdObjectId, Caller("entra-user"));
        var composite = new CompositeVerifiedCallerDirectory([entra, phone],
            A.Fake<ILogger<CompositeVerifiedCallerDirectory>>());

        var result = await composite.ResolveAsync(TenantId, Sender(ChannelIdentifierKind.EmailAddress));

        Assert.Null(result);
        A.CallTo(() => phone.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => entra.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task First_non_null_resolution_wins_when_several_leaves_own_the_kind()
    {
        var first = Leaf(ChannelIdentifierKind.PhoneNumber, resolves: null);
        var second = Leaf(ChannelIdentifierKind.PhoneNumber, Caller("second-user"));
        var composite = new CompositeVerifiedCallerDirectory([first, second],
            A.Fake<ILogger<CompositeVerifiedCallerDirectory>>());

        var result = await composite.ResolveAsync(TenantId, Sender(ChannelIdentifierKind.PhoneNumber));

        Assert.NotNull(result);
        Assert.Equal("second-user", result!.Principal.SubjectId);
    }
}

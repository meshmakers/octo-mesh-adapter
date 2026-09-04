using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CallerBinding;

/// <summary>
///     Proves the channel-side caller binder (AB#5126) combines the directory lookup with the
///     three-state decision the way every channel trigger relies on: it only queries the directory
///     when the mode wants a binding, runs as the resolved caller when one exists, falls back to the
///     service account under the permissive modes, and rejects a required-but-unresolved sender.
/// </summary>
public class ChannelCallerBinderTests
{
    private const string TenantId = "acme";

    private static readonly ChannelSender Sender =
        new(ChannelIdentifierKind.EmailAddress, "u@example.com", CallerTrustLevel.Weak);

    private readonly IVerifiedCallerDirectory _directory = A.Fake<IVerifiedCallerDirectory>();
    private readonly ChannelCallerBinder _binder;

    public ChannelCallerBinderTests()
    {
        _binder = new ChannelCallerBinder(_directory, A.Fake<ILogger<ChannelCallerBinder>>());
    }

    private void DirectoryResolvesTo(ResolvedCaller? resolved)
        => A.CallTo(() => _directory.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .Returns(Task.FromResult(resolved));

    [Fact]
    public async Task AnonymousAllowed_never_queries_the_directory()
    {
        var result = await _binder.BindAsync(TenantId, CallerBindingMode.AnonymousAllowed, Sender);

        Assert.False(result.Rejected);
        Assert.Null(result.Principal);
        Assert.Equal(CallerTrustLevel.None, result.Trust);
        A.CallTo(() => _directory.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task BindingOptional_runs_as_the_resolved_caller_when_a_binding_exists()
    {
        var principal = new VerifiedPrincipal("user-1", TenantId, "u@example.com", "U", ["Reader"]);
        DirectoryResolvesTo(new ResolvedCaller(principal, CallerTrustLevel.Strong));

        var result = await _binder.BindAsync(TenantId, CallerBindingMode.BindingOptional, Sender);

        Assert.False(result.Rejected);
        Assert.Same(principal, result.Principal);
        Assert.Equal(CallerTrustLevel.Strong, result.Trust);
    }

    [Fact]
    public async Task BindingOptional_falls_back_to_the_service_account_when_unresolved()
    {
        DirectoryResolvesTo(null);

        var result = await _binder.BindAsync(TenantId, CallerBindingMode.BindingOptional, Sender);

        Assert.False(result.Rejected);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task BindingRequired_rejects_when_the_sender_cannot_be_resolved()
    {
        DirectoryResolvesTo(null);

        var result = await _binder.BindAsync(TenantId, CallerBindingMode.BindingRequired, Sender);

        Assert.True(result.Rejected);
        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task BindingRequired_rejects_when_there_is_no_single_sender()
    {
        // A batch trigger that could not pin a single sender passes null — treated as unresolved, so
        // a required binding refuses rather than running as the service account.
        var result = await _binder.BindAsync(TenantId, CallerBindingMode.BindingRequired, sender: null);

        Assert.True(result.Rejected);
        A.CallTo(() => _directory.ResolveAsync(A<string>._, A<ChannelSender>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}

using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;

/// <summary>
///     The delivery-state lease against a real MongoDB. A run that decided from a stale read must lose
///     to the run that wrote first, whether the state already existed or both runs were about to create it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class NotificationLeaseIntegrationTests(NotificationEngineFixture fixture)
    : IClassFixture<NotificationEngineFixture>
{
    private static readonly TimeSpan Expiry = NotificationDigest.LeaseExpiry;

    private async Task<(NotificationEngineHarness Harness, OctoObjectId Preference)> ScenarioAsync()
    {
        fixture.EnsureInitialized();
        var h = new NotificationEngineHarness(fixture);
        await h.CreateSubscriptionAsync();
        return (h, await h.CreatePreferenceAsync("user-a"));
    }

    private static DateTime Ms(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    [Fact]
    public async Task WithoutAState_TheLeaseIsTakenOnTheStateItCreates()
    {
        var (h, preference) = await ScenarioAsync();

        var leased = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), null, preference,
            h.Clock.UtcNow, Expiry);

        leased.Should().Be(NotificationDeliveryStateStore.StateRtIdFor(preference));
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(Ms(h.Clock.UtcNow));
        (await h.StateCountAsync(preference)).Should().Be(1);
    }

    [Fact]
    public async Task AnExistingStateWithAFreeLease_IsLeased()
    {
        var (h, preference) = await ScenarioAsync();
        var state = await SaveAsync(h, preference, null);

        var leased = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), state, preference,
            h.Clock.UtcNow, Expiry);

        leased.Should().Be(state);
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(Ms(h.Clock.UtcNow));
    }

    [Fact]
    public async Task ALeaseHeldByAnotherRun_IsNotTaken_AndStaysThatRunsLease()
    {
        var (h, preference) = await ScenarioAsync();
        var othersLease = Ms(h.Clock.UtcNow.AddMinutes(-2));
        var state = await SaveAsync(h, preference, othersLease);

        var leased = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), state, preference,
            h.Clock.UtcNow, Expiry);

        leased.Should().BeNull();
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(othersLease);
    }

    [Fact]
    public async Task AnExpiredLease_IsTakenOver()
    {
        var (h, preference) = await ScenarioAsync();
        var state = await SaveAsync(h, preference, Ms(h.Clock.UtcNow.AddMinutes(-11)));

        var leased = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), state, preference,
            h.Clock.UtcNow, Expiry);

        leased.Should().Be(state);
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(Ms(h.Clock.UtcNow));
    }

    [Fact]
    public async Task TwoRunsThatBothSawAFreeLease_OnlyTheFirstWriterGetsIt()
    {
        var (h, preference) = await ScenarioAsync();
        var state = await SaveAsync(h, preference, null);
        var first = h.Clock.UtcNow;
        var second = first.AddSeconds(1);

        var won = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), state, preference,
            first, Expiry);
        var lost = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), state, preference,
            second, Expiry);

        won.Should().Be(state);
        lost.Should().BeNull();
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(Ms(first));
    }

    [Fact]
    public async Task TwoRunsThatBothSawNoState_CreateOneState_AndOnlyTheFirstGetsTheLease()
    {
        var (h, preference) = await ScenarioAsync();
        var first = h.Clock.UtcNow;

        var won = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), null, preference,
            first, Expiry);
        var lost = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), null, preference,
            first.AddSeconds(1), Expiry);

        won.Should().NotBeNull();
        lost.Should().BeNull();
        (await h.StateCountAsync(preference)).Should().Be(1);
        (await h.StateOfAsync(preference))!.RunningSince.Should().Be(Ms(first));
    }

    [Fact]
    public async Task ConcurrentRuns_ExactlyOneWins_WithAndWithoutAState()
    {
        for (var round = 0; round < 5; round++)
        {
            var (h, preference) = await ScenarioAsync();
            var existing = round % 2 == 0 ? (OctoObjectId?)null : await SaveAsync(h, preference, null);
            var now = h.Clock.UtcNow;

            var results = await Task.WhenAll(
                NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), existing, preference, now,
                    Expiry),
                NotificationDeliveryStateStore.TryAcquireLeaseAsync(h.CreateEtlContext(), existing, preference,
                    now.AddMilliseconds(5), Expiry));

            results.Count(result => result != null).Should().Be(1, $"round {round}");
            (await h.StateCountAsync(preference)).Should().Be(1, $"round {round}");
        }
    }

    [Fact]
    public async Task ASaveOfARunThatSawNoState_UpdatesTheStateAnotherRunCreated()
    {
        var (h, preference) = await ScenarioAsync();
        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), null, preference,
            new NotificationDeliveryStateChange(SentRows: [new SentRow("a", h.Clock.UtcNow, h.Clock.UtcNow)], LastRunAt: h.Clock.UtcNow));

        var saved = await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), null, preference,
            new NotificationDeliveryStateChange(LastError: "boom"));

        saved.Should().Be(NotificationDeliveryStateStore.StateRtIdFor(preference));
        (await h.StateCountAsync(preference)).Should().Be(1);
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Be("boom");
        state.SentRows!.Select(row => row.RowRtId).Should().Equal("a");
    }

    [Fact]
    public async Task AWriteWithoutTheLease_LeavesARunningLeaseAndItsStateAlone()
    {
        var (h, preference) = await ScenarioAsync();
        var othersLease = Ms(h.Clock.UtcNow.AddMinutes(-2));
        var state = await SaveAsync(h, preference, othersLease);

        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), state, preference,
            new NotificationDeliveryStateChange(LastError: "stale"),
            NotificationDeliveryStateStore.Unleased(h.Clock.UtcNow, Expiry));

        var after = (await h.StateOfAsync(preference))!;
        after.RunningSince.Should().Be(othersLease);
        after.LastError.Should().BeNull();
    }

    [Fact]
    public async Task AWriteWithoutTheLease_IsAppliedWhileNoRunHoldsIt_AndKeepsTheLeaseField()
    {
        var (h, preference) = await ScenarioAsync();
        var expired = Ms(h.Clock.UtcNow.AddMinutes(-11));
        var state = await SaveAsync(h, preference, expired);

        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), state, preference,
            new NotificationDeliveryStateChange(LastError: "boom"),
            NotificationDeliveryStateStore.Unleased(h.Clock.UtcNow, Expiry));

        var after = (await h.StateOfAsync(preference))!;
        after.LastError.Should().Be("boom");
        after.RunningSince.Should().Be(expired);
    }

    [Fact]
    public async Task AWriteOfARunWhoseLeaseWasTakenOver_LeavesTheNewerRunsLeaseAndStateAlone()
    {
        var (h, preference) = await ScenarioAsync();
        var oldLease = Ms(h.Clock.UtcNow.AddMinutes(-12));
        var newLease = Ms(h.Clock.UtcNow.AddMinutes(-1));
        var state = await SaveAsync(h, preference, newLease);

        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), state, preference,
            new NotificationDeliveryStateChange(LastSentAt: h.Clock.UtcNow, ReleaseLease: true),
            NotificationDeliveryStateStore.HeldBy(oldLease));

        var after = (await h.StateOfAsync(preference))!;
        after.RunningSince.Should().Be(newLease);
        after.LastSentAt.Should().BeNull();
    }

    [Fact]
    public async Task AWriteOfTheLeaseHolder_IsAppliedAndReleasesTheLease()
    {
        var (h, preference) = await ScenarioAsync();
        var lease = Ms(h.Clock.UtcNow.AddMinutes(-1));
        var state = await SaveAsync(h, preference, lease);

        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), state, preference,
            new NotificationDeliveryStateChange(LastSentAt: h.Clock.UtcNow, ReleaseLease: true),
            NotificationDeliveryStateStore.HeldBy(lease));

        var after = (await h.StateOfAsync(preference))!;
        after.RunningSince.Should().BeNull();
        after.LastSentAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
    }

    private static Task<OctoObjectId> SaveAsync(NotificationEngineHarness h, OctoObjectId preference,
        DateTime? runningSince) =>
        NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), null, preference,
            new NotificationDeliveryStateChange(LastRunAt: h.Clock.UtcNow, RunningSince: runningSince));
}

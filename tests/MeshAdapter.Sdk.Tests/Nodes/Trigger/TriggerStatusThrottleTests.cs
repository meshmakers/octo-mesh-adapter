using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5619 / AB#5620: every status report is a write on the pipeline entity (AB#5618), so the
/// throttle decides which trigger events are worth one. Pinned: first event, transitions, activity
/// and heartbeat; nothing else.
/// </summary>
public class TriggerStatusThrottleTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(300);

    [Fact]
    public void FirstEvent_IsAlwaysReported()
    {
        var sut = new TriggerStatusThrottle(Heartbeat, TimeSpan.Zero);

        Assert.True(sut.ShouldReport(T0, isError: false, hasActivity: false));
    }

    [Fact]
    public void IdleSuccessPolls_AreReportedOnlyAsHeartbeat()
    {
        var sut = new TriggerStatusThrottle(Heartbeat, TimeSpan.Zero);
        Assert.True(sut.ShouldReport(T0, false, false));

        // A 5-s poll for just under five minutes: not one write.
        for (var s = 5; s < 300; s += 5)
        {
            Assert.False(sut.ShouldReport(T0.AddSeconds(s), false, false));
        }

        Assert.True(sut.ShouldReport(T0.AddSeconds(300), false, false));
        Assert.False(sut.ShouldReport(T0.AddSeconds(305), false, false));
    }

    [Fact]
    public void Transition_IsReportedImmediately_BothWays()
    {
        var sut = new TriggerStatusThrottle(Heartbeat, TimeSpan.Zero);
        Assert.True(sut.ShouldReport(T0, false, false));

        Assert.True(sut.ShouldReport(T0.AddSeconds(5), isError: true, hasActivity: false));
        Assert.True(sut.ShouldReport(T0.AddSeconds(10), isError: false, hasActivity: false));
    }

    [Fact]
    public void RepeatedErrors_AreReportedOnlyAsHeartbeat()
    {
        var sut = new TriggerStatusThrottle(Heartbeat, TimeSpan.Zero);
        Assert.True(sut.ShouldReport(T0, true, false));

        Assert.False(sut.ShouldReport(T0.AddSeconds(35), true, false));
        Assert.False(sut.ShouldReport(T0.AddSeconds(299), true, false));
        Assert.True(sut.ShouldReport(T0.AddSeconds(300), true, false));
    }

    [Fact]
    public void Activity_WithZeroActivityInterval_IsAlwaysReported()
    {
        var sut = new TriggerStatusThrottle(Heartbeat, TimeSpan.Zero);
        Assert.True(sut.ShouldReport(T0, false, false));

        Assert.True(sut.ShouldReport(T0.AddSeconds(5), false, hasActivity: true));
        Assert.True(sut.ShouldReport(T0.AddSeconds(10), false, hasActivity: true));
        Assert.False(sut.ShouldReport(T0.AddSeconds(15), false, hasActivity: false));
    }

    [Fact]
    public void ActivityBurst_IsThrottledToTheActivityInterval()
    {
        // FromTeamsBot@1: one report per processed activity, but at most one per 30 s.
        var sut = new TriggerStatusThrottle(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Assert.True(sut.ShouldReport(T0, false, true));

        Assert.False(sut.ShouldReport(T0.AddSeconds(1), false, true));
        Assert.False(sut.ShouldReport(T0.AddSeconds(29), false, true));
        Assert.True(sut.ShouldReport(T0.AddSeconds(30), false, true));
    }

    [Fact]
    public void SuppressedEvent_DoesNotMoveTheWindow()
    {
        var sut = new TriggerStatusThrottle(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Assert.True(sut.ShouldReport(T0, false, true));
        Assert.False(sut.ShouldReport(T0.AddSeconds(20), false, true));

        // 30 s after the last REPORT, not after the last suppressed event.
        Assert.True(sut.ShouldReport(T0.AddSeconds(30), false, true));
    }

    [Fact]
    public async Task ConcurrentEvents_ReportExactlyOnce()
    {
        var sut = new TriggerStatusThrottle(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => sut.ShouldReport(T0, false, true), TestContext.Current.CancellationToken)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(-5, 300)]
    [InlineData(60, 60)]
    public void ResolveInterval_NonPositiveFallsBackToTheDefault(int configured, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TriggerStatusThrottle.ResolveInterval(configured, 300));
    }

    [Fact]
    public void Constructor_RejectsANonPositiveHeartbeat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerStatusThrottle(TimeSpan.Zero, TimeSpan.Zero));
    }
}

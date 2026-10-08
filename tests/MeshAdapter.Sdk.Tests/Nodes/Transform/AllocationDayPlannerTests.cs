using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the calendar arithmetic of AllocateCommunityEnergy@1 (AB#5634): local days in Europe/Vienna,
/// DST days with 92 / 100 slots, the cut-off in UTC and the automatic day selection.
/// </summary>
public class AllocationDayPlannerTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    private static AllocationDayPlanner Planner(int offset = 1, int cutOffHour = 6)
        => new(Vienna, offset, TimeSpan.FromHours(cutOffHour), TimeSpan.FromMinutes(15));

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0)
        => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void NormalWinterDay_Has96Slots_StartingAtLocalMidnight()
    {
        var day = Planner().BuildDay(new DateOnly(2026, 11, 15));

        Assert.Equal(96, day.Slots.Count);
        Assert.Equal(Utc(2026, 11, 14, 23), day.StartUtc);
        Assert.Equal(Utc(2026, 11, 15, 23), day.EndUtc);
        Assert.Equal(day.StartUtc, day.Slots[0].From);
        Assert.Equal(day.EndUtc, day.Slots[^1].To);
        Assert.All(day.Slots, s => Assert.Equal(TimeSpan.FromMinutes(15), s.To - s.From));
        Assert.All(day.Slots, s => Assert.Equal(DateTimeKind.Utc, s.From.Kind));
    }

    [Fact]
    public void NormalSummerDay_Has96Slots_StartingAt22Utc()
    {
        var day = Planner().BuildDay(new DateOnly(2026, 7, 1));

        Assert.Equal(96, day.Slots.Count);
        Assert.Equal(Utc(2026, 6, 30, 22), day.StartUtc);
    }

    [Fact]
    public void SpringForwardDay_Has92Slots()
    {
        var day = Planner().BuildDay(new DateOnly(2026, 3, 29));

        Assert.Equal(92, day.Slots.Count);
        Assert.Equal(Utc(2026, 3, 28, 23), day.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 22), day.EndUtc);
    }

    [Fact]
    public void FallBackDay_Has100Slots()
    {
        var day = Planner().BuildDay(new DateOnly(2026, 10, 25));

        Assert.Equal(100, day.Slots.Count);
        Assert.Equal(Utc(2026, 10, 24, 22), day.StartUtc);
        Assert.Equal(Utc(2026, 10, 25, 23), day.EndUtc);
    }

    [Fact]
    public void ConsecutiveDays_AreContiguous()
    {
        var days = Planner().Plan(new DateOnly(2026, 3, 28), 3);

        Assert.Equal([96, 92, 96], days.Select(d => d.Slots.Count));
        Assert.Equal(days[0].EndUtc, days[1].StartUtc);
        Assert.Equal(days[1].EndUtc, days[2].StartUtc);
    }

    [Fact]
    public void CutOff_Winter_IsNextDay0600LocalAs0500Utc()
    {
        Assert.Equal(Utc(2026, 11, 16, 5), Planner().CutOffUtc(new DateOnly(2026, 11, 15)));
        Assert.Equal(Utc(2026, 11, 16, 5), Planner().BuildDay(new DateOnly(2026, 11, 15)).CutOffUtc);
    }

    [Fact]
    public void CutOff_Summer_IsNextDay0600LocalAs0400Utc()
    {
        Assert.Equal(Utc(2026, 7, 2, 4), Planner().CutOffUtc(new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void CutOff_HonoursOffsetAndTime()
    {
        Assert.Equal(Utc(2026, 11, 17, 21), Planner(offset: 2, cutOffHour: 22).CutOffUtc(new DateOnly(2026, 11, 15)));
    }

    [Fact]
    public void AutomaticDay_BeforeCutOff_IsTheDayBeforeYesterday()
    {
        // 2026-11-16 04:59Z = 05:59 local, before the 06:00 cut-off of 2026-11-15.
        Assert.Equal(new DateOnly(2026, 11, 14), Planner().LatestClosedDay(Utc(2026, 11, 16, 4, 59)));
    }

    [Fact]
    public void AutomaticDay_AtAndAfterCutOff_IsYesterday()
    {
        Assert.Equal(new DateOnly(2026, 11, 15), Planner().LatestClosedDay(Utc(2026, 11, 16, 5)));
        Assert.Equal(new DateOnly(2026, 11, 15), Planner().LatestClosedDay(Utc(2026, 11, 16, 22, 59)));
    }

    [Fact]
    public void AutomaticDay_Summer_UsesTheSummerOffset()
    {
        Assert.Equal(new DateOnly(2026, 6, 30), Planner().LatestClosedDay(Utc(2026, 7, 2, 3, 59)));
        Assert.Equal(new DateOnly(2026, 7, 1), Planner().LatestClosedDay(Utc(2026, 7, 2, 4)));
    }

    [Fact]
    public void AutomaticDay_JustAfterLocalMidnight_StillFindsTheRightDay()
    {
        // 2026-11-16 23:30Z is already 2026-11-17 00:30 local; the latest closed day is 2026-11-15.
        Assert.Equal(new DateOnly(2026, 11, 15), Planner().LatestClosedDay(Utc(2026, 11, 16, 23, 30)));
    }

    [Fact]
    public void SlotLengthThatDoesNotDivideTheDay_Throws()
    {
        var planner = new AllocationDayPlanner(Vienna, 1, TimeSpan.FromHours(6), TimeSpan.FromMinutes(7));
        Assert.Throws<InvalidOperationException>(() => planner.BuildDay(new DateOnly(2026, 11, 15)));
    }
}

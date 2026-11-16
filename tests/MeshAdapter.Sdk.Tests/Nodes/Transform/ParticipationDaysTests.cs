using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// The participation rule of the community nodes (AB#8375): <c>ParticipationPeriod.TimeRange.To</c>
/// names the last participating day, inclusive, and <c>From</c> the first one, both as the calendar
/// date in the node's time zone. Both stored forms of a day D (<c>D 00:00Z</c> and 00:00 Vienna) mean D.
/// </summary>
public class ParticipationDaysTests
{
    [Theory]
    [MemberData(nameof(ParticipationDayForms.Days), MemberType = typeof(ParticipationDayForms))]
    public void BothStoredForms_NameTheSameDay(string day, int slots, bool viennaForm)
    {
        var d = DateOnly.Parse(day);
        var stored = ParticipationDayForms.Stored(d, viennaForm);

        Assert.Equal(d, ParticipationDays.LocalDay(stored, ParticipationDayForms.Vienna));
        Assert.Equal(slots, ParticipationDayForms.SlotStarts(d).Count);
    }

    [Theory]
    [MemberData(nameof(ParticipationDayForms.Days), MemberType = typeof(ParticipationDayForms))]
    public void EverySlotOfTheLastDay_Participates_NoSlotOfTheNextDay(string day, int slots, bool viennaForm)
    {
        var d = DateOnly.Parse(day);
        var member = Member(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ParticipationDayForms.Stored(d, viennaForm));

        var lastDay = ParticipationDayForms.SlotStarts(d);
        Assert.Equal(slots, lastDay.Count(s => member.Participates(ParticipationDayForms.Slot(s))));
        Assert.DoesNotContain(ParticipationDayForms.SlotStarts(d.AddDays(1)),
            s => member.Participates(ParticipationDayForms.Slot(s)));
    }

    [Theory]
    [MemberData(nameof(ParticipationDayForms.Days), MemberType = typeof(ParticipationDayForms))]
    public void TheFirstDay_StartsAtLocalMidnight(string day, int slots, bool viennaForm)
    {
        var d = DateOnly.Parse(day);
        var member = Member(ParticipationDayForms.Stored(d, viennaForm), null);

        Assert.DoesNotContain(ParticipationDayForms.SlotStarts(d.AddDays(-1)),
            s => member.Participates(ParticipationDayForms.Slot(s)));
        Assert.Equal(slots, ParticipationDayForms.SlotStarts(d).Count(s => member.Participates(ParticipationDayForms.Slot(s))));
    }

    [Fact]
    public void OpenPeriod_HasNoLimits()
    {
        var member = Member(null, null);
        Assert.True(member.Participates(ParticipationDayForms.Slot(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.True(member.Participates(ParticipationDayForms.Slot(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public void UnspecifiedKind_IsReadAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 15, 23, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateOnly(2026, 11, 16), ParticipationDays.LocalDay(unspecified, ParticipationDayForms.Vienna));
    }

    private static CommunityMember Member(DateTime? from, DateTime? to)
        => new("0000000000000000000000c1", CommunityMemberKind.Consumer, "AT1", 100, 1,
            [ParticipationDays.From(from, to, ParticipationDayForms.Vienna)], ParticipationDayForms.Vienna);
}

/// <summary>Calendar helpers for the AB#8375 tests of the three community nodes.</summary>
internal static class ParticipationDayForms
{
    public static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    /// <summary>Day, its number of quarter hours in Vienna, and whether <c>To</c>/<c>From</c> is stored as 00:00 Vienna (else D 00:00Z).</summary>
    public static TheoryData<string, int, bool> Days => new()
    {
        { "2026-11-15", 96, true },
        { "2026-11-15", 96, false },
        { "2026-03-29", 92, true },
        { "2026-03-29", 92, false },
        { "2026-10-25", 100, true },
        { "2026-10-25", 100, false }
    };

    /// <summary>00:00 Vienna of <paramref name="day"/> as a UTC instant.</summary>
    public static DateTime LocalMidnightUtc(DateOnly day)
        => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Vienna);

    /// <summary>The stored form of a participation boundary on <paramref name="day"/>.</summary>
    public static DateTime Stored(DateOnly day, bool viennaForm)
        => viennaForm ? LocalMidnightUtc(day) : DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>The UTC starts of every quarter hour of the Vienna day.</summary>
    public static List<DateTime> SlotStarts(DateOnly day)
    {
        var start = LocalMidnightUtc(day);
        var end = LocalMidnightUtc(day.AddDays(1));
        var slots = new List<DateTime>();
        for (var t = start; t < end; t = t.AddMinutes(15))
        {
            slots.Add(t);
        }

        return slots;
    }

    public static AllocationSlot Slot(DateTime from) => new(from, from.AddMinutes(15));
}

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

/// <summary>One slot of a day, UTC, half-open <c>[From, To)</c>.</summary>
internal readonly record struct AllocationSlot(DateTime From, DateTime To);

/// <summary>One calendar day of the time zone, cut into UTC slots.</summary>
/// <param name="Day">The local calendar day.</param>
/// <param name="StartUtc">UTC instant of local midnight at the start of the day.</param>
/// <param name="EndUtc">UTC instant of local midnight at the start of the next day.</param>
/// <param name="CutOffUtc">Cut-off of the day, UTC; also the creation time of its records.</param>
/// <param name="Slots">The slots, ascending; 92, 96 or 100 for 15 minutes in Europe/Vienna.</param>
internal sealed record AllocationDay(
    DateOnly Day,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime CutOffUtc,
    IReadOnlyList<AllocationSlot> Slots);

/// <summary>
/// Pure calendar arithmetic of <c>AllocateCommunityEnergy@1</c>: cuts local days into UTC slots
/// (DST days included), computes the cut-off of a day and picks the day to allocate automatically.
/// </summary>
internal sealed class AllocationDayPlanner
{
    private readonly TimeZoneInfo _timeZone;
    private readonly int _cutOffDayOffset;
    private readonly TimeSpan _cutOffTime;
    private readonly TimeSpan _slotLength;

    public AllocationDayPlanner(TimeZoneInfo timeZone, int cutOffDayOffset, TimeSpan cutOffTime, TimeSpan slotLength)
    {
        if (slotLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(slotLength), slotLength, "The slot length must be positive.");
        }

        if (cutOffTime < TimeSpan.Zero || cutOffTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(cutOffTime), cutOffTime,
                "The cut-off time must be a time of day.");
        }

        _timeZone = timeZone;
        _cutOffDayOffset = cutOffDayOffset;
        _cutOffTime = cutOffTime;
        _slotLength = slotLength;
    }

    /// <summary>Builds <paramref name="numDays"/> consecutive days starting at <paramref name="firstDay"/>.</summary>
    public IReadOnlyList<AllocationDay> Plan(DateOnly firstDay, int numDays)
        => Enumerable.Range(0, numDays).Select(i => BuildDay(firstDay.AddDays(i))).ToList();

    /// <summary>Cuts one local day into slots.</summary>
    public AllocationDay BuildDay(DateOnly day)
    {
        var start = LocalToUtc(day.ToDateTime(TimeOnly.MinValue));
        var end = LocalToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var length = end - start;
        if (length.Ticks % _slotLength.Ticks != 0)
        {
            throw new InvalidOperationException(
                $"AllocateCommunityEnergy: day {day:yyyy-MM-dd} in '{_timeZone.Id}' lasts {length}, which is not a " +
                $"multiple of the slot length {_slotLength}.");
        }

        var count = (int)(length.Ticks / _slotLength.Ticks);
        var slots = new AllocationSlot[count];
        for (var i = 0; i < count; i++)
        {
            var from = start.AddTicks(_slotLength.Ticks * i);
            slots[i] = new AllocationSlot(from, from.Add(_slotLength));
        }

        return new AllocationDay(day, start, end, CutOffUtc(day), slots);
    }

    /// <summary>Cut-off of day D: local (D + offset) at the cut-off time, in UTC.</summary>
    public DateTime CutOffUtc(DateOnly day)
        => LocalToUtc(day.AddDays(_cutOffDayOffset).ToDateTime(TimeOnly.FromTimeSpan(_cutOffTime)));

    /// <summary>The latest day whose cut-off is not after <paramref name="utcNow"/>.</summary>
    public DateOnly LatestClosedDay(DateTime utcNow)
    {
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, _timeZone));

        // The cut-off of D lies on local day D + offset, so the candidate starts there and walks back.
        var candidate = localToday.AddDays(-_cutOffDayOffset + 1);
        for (var guard = 0; guard < 10; guard++)
        {
            if (CutOffUtc(candidate) <= now)
            {
                return candidate;
            }

            candidate = candidate.AddDays(-1);
        }

        throw new InvalidOperationException("AllocateCommunityEnergy: no closed day found.");
    }

    /// <summary>
    /// Local wall clock to UTC. A wall clock skipped by a forward transition is moved forward to the
    /// first valid minute; an ambiguous one (fall-back) resolves to its first occurrence.
    /// </summary>
    private DateTime LocalToUtc(DateTime local)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var guard = 0; guard < 24 * 60 && _timeZone.IsInvalidTime(wallClock); guard++)
        {
            wallClock = wallClock.AddMinutes(1);
        }

        if (_timeZone.IsAmbiguousTime(wallClock))
        {
            var offset = _timeZone.GetAmbiguousTimeOffsets(wallClock).Max();
            return DateTime.SpecifyKind(wallClock - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(wallClock, _timeZone);
    }
}

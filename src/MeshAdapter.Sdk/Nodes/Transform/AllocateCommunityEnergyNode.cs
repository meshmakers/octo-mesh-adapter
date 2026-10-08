using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Distributes the generation offered to an energy community among its participating consumers for
/// whole calendar days (AB#5634, requirements ALC-01..08). Reads the raw consumer and producer
/// registers from a time-range archive and emits, per metering point, register and day, a record in
/// the EDA ingest shape that <c>SaveTimeRangeSeriesInArchive@1</c> writes unchanged.
/// </summary>
/// <remarks>
/// The algorithm itself lives in <see cref="CommunityAllocator"/> and the calendar arithmetic in
/// <see cref="AllocationDayPlanner"/>, both free of I/O; this class only loads, joins and shapes.
/// </remarks>
[NodeConfiguration(typeof(AllocateCommunityEnergyNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class AllocateCommunityEnergyNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext) : IPipelineNode
{
    private const string NodeName = "AllocateCommunityEnergy";

    /// <summary>Rows read per archive page. The repository applies no default cap; paging bounds each read.</summary>
    internal const int ArchivePageSize = 10_000;

    /// <summary>Clock used for the automatic day selection; replaced by tests.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Page size of the archive read; lowered by tests to exercise the paging.</summary>
    internal int PageSize { get; init; } = ArchivePageSize;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<AllocateCommunityEnergyNodeConfiguration>();

        // ---------------------------------------------------------------- window
        var timeZoneId = ResolveString(dataContext, c.TimeZonePath) ?? c.TimeZone;
        var timeZone = CommunityValues.ResolveTimeZone(timeZoneId, nodeContext, NodeName);

        var cutOffTime = CommunityValues.ResolveTimeOfDay(dataContext, nodeContext, c.CutOffTimePath, NodeName)
                         ?? c.CutOffTime;
        var planner = new AllocationDayPlanner(timeZone, c.CutOffDayOffset, cutOffTime, c.SlotLength);

        var startDay = ResolveDay(dataContext, nodeContext, c.StartDayPath) ?? ToDay(c.StartDay);
        int numDays;
        if (startDay is null)
        {
            startDay = planner.LatestClosedDay(Clock.GetUtcNow().UtcDateTime);
            numDays = 1;
            nodeContext.Debug($"{NodeName}: no start day given, allocating the latest closed day {startDay:yyyy-MM-dd}.");
        }
        else
        {
            numDays = StreamDataNodeHelpers.ResolveIntFromPath(dataContext, nodeContext, c.NumDaysPath,
                          nameof(c.NumDaysPath), "the configured NumDays is used.")
                      ?? c.NumDays ?? 1;
        }

        if (numDays <= 0)
        {
            throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {NodeName}: NumDays must be greater than 0 (got {numDays}).");
        }

        if (numDays > c.MaxDays)
        {
            throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {NodeName}: NumDays {numDays} exceeds MaxDays {c.MaxDays}.");
        }

        var days = planner.Plan(startDay.Value, numDays);
        var allocator = new CommunityAllocator(c.Resolution, c.MissingQuality);

        // ---------------------------------------------------------------- runtime model
        var members = await LoadMembersAsync(c, nodeContext);

        // ---------------------------------------------------------------- per-day decision (ALC-05, ALC-08)
        var summaries = new List<AllocationDaySummary>();
        var daysToAllocate = new List<(AllocationDay Day, List<CommunityMember> Members, int SummaryIndex)>();
        foreach (var day in days)
        {
            var participating = members.Where(m => day.Slots.Any(m.Participates)).ToList();
            var consumers = participating.Count(m => m.Kind == CommunityMemberKind.Consumer);
            var producers = participating.Count - consumers;

            if (participating.Count == 0)
            {
                summaries.Add(new AllocationDaySummary
                {
                    Day = FormatDay(day.Day), Status = AllocationDaySummary.StatusEmpty,
                    Reason = "No consumer or producer participates.", Slots = day.Slots.Count,
                    CutOff = day.CutOffUtc
                });
                continue;
            }

            var blocked = participating
                .Where(m => !c.AllocatableDataSources.Contains(m.DataSource))
                .OrderBy(m => m.RtId, StringComparer.Ordinal)
                .ToList();
            if (blocked.Count > 0)
            {
                var names = string.Join(", ", blocked.Select(m => m.MeteringPointNumber ?? m.RtId));
                var reason = $"Participating metering point(s) with a data source that is not allocatable: {names}.";
                nodeContext.Warning($"{NodeName}: day {FormatDay(day.Day)} aborted (ALC-08). {reason}");
                summaries.Add(new AllocationDaySummary
                {
                    Day = FormatDay(day.Day), Status = AllocationDaySummary.StatusAborted, Reason = reason,
                    Slots = day.Slots.Count, Consumers = consumers, Producers = producers,
                    CutOff = day.CutOffUtc
                });
                continue;
            }

            daysToAllocate.Add((day, participating, summaries.Count));
            summaries.Add(null!); // filled in after the allocation below, keeping day order
        }

        // ---------------------------------------------------------------- archive
        var values = daysToAllocate.Count == 0
            ? new Dictionary<(string, DateTime), CommunityRawValue>()
            : await ReadInputsAsync(c, nodeContext, daysToAllocate, c.SlotLength);

        // ---------------------------------------------------------------- allocation
        var records = new List<AllocationEnergyData>();
        var negativeValues = 0;
        foreach (var (day, participating, summaryIndex) in daysToAllocate)
        {
            var (dayRecords, summary, negatives) = AllocateDay(c, allocator, day, participating, values);
            records.AddRange(dayRecords);
            summaries[summaryIndex] = summary;
            negativeValues += negatives;
        }

        if (negativeValues > 0)
        {
            nodeContext.Warning($"{NodeName}: {negativeValues} negative input value(s) were treated as 0.");
        }

        nodeContext.Debug(
            $"{NodeName}: {records.Count} record(s) for {days.Count} day(s) " +
            $"({summaries.Count(s => s.Status == AllocationDaySummary.StatusAllocated)} allocated, " +
            $"{summaries.Count(s => s.Status == AllocationDaySummary.StatusAborted)} aborted, " +
            $"{summaries.Count(s => s.Status == AllocationDaySummary.StatusEmpty)} empty).");

        dataContext.Set(c.TargetPath, records, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        if (!string.IsNullOrWhiteSpace(c.SummaryTargetPath))
        {
            dataContext.Set(c.SummaryTargetPath, summaries, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
        }

        await next(dataContext, nodeContext);
    }

    // ==================================================================== allocation of one day

    private static (List<AllocationEnergyData> Records, AllocationDaySummary Summary, int Negatives) AllocateDay(
        AllocateCommunityEnergyNodeConfiguration c, CommunityAllocator allocator, AllocationDay day,
        List<CommunityMember> participating, IReadOnlyDictionary<(string, DateTime), CommunityRawValue> values)
    {
        // Deterministic member order: rtId ordinal. The allocator's tie breaker uses the same order.
        var ordered = participating.OrderBy(m => m.RtId, StringComparer.Ordinal).ToList();
        var share = ordered.ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var offered = ordered.Where(m => m.Kind == CommunityMemberKind.Producer)
            .ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var surplus = ordered.Where(m => m.Kind == CommunityMemberKind.Producer)
            .ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var byRtId = ordered.ToDictionary(m => m.RtId);

        decimal consumption = 0, eligible = 0;
        long offeredUnits = 0, allocatedUnits = 0, surplusUnits = 0;
        int missingConsumers = 0, missingProducers = 0, negatives = 0;

        foreach (var slot in day.Slots)
        {
            var result = CommunitySlotAllocation.Allocate(allocator, slot, ordered, values, c.MissingQuality,
                out _, out _);
            if (result is null)
            {
                continue;
            }

            consumption += result.ConsumptionKWh;
            eligible += result.EligibleKWh;
            offeredUnits += result.OfferedUnits;
            allocatedUnits += result.AllocatedUnits;
            surplusUnits += result.SurplusUnits;
            negatives += result.NegativeValues;

            foreach (var a in result.Consumers)
            {
                if (a.Missing) missingConsumers++;
                share[a.RtId].Add(Quantity(byRtId[a.RtId], slot, allocator.ToKWh(a.ShareUnits), a.Quality));
            }

            foreach (var p in result.Producers)
            {
                if (p.Missing) missingProducers++;
                var m = byRtId[p.RtId];
                offered[p.RtId].Add(Quantity(m, slot, allocator.ToKWh(p.OfferedUnits), p.Quality));
                surplus[p.RtId].Add(Quantity(m, slot, allocator.ToKWh(p.SurplusUnits), p.Quality));
            }
        }

        var records = new List<AllocationEnergyData>();
        foreach (var m in ordered)
        {
            if (m.Kind == CommunityMemberKind.Consumer)
            {
                AddRecord(records, c, day, m, c.ConsumerShareObis, share[m.RtId]);
            }
            else
            {
                AddRecord(records, c, day, m, c.ProducerOfferedObis, offered[m.RtId]);
                AddRecord(records, c, day, m, c.ProducerSurplusObis, surplus[m.RtId]);
            }
        }

        var allocatedKWh = allocator.ToKWh(allocatedUnits);
        var offeredKWh = allocator.ToKWh(offeredUnits);
        var surplusKWh = allocator.ToKWh(surplusUnits);
        var summary = new AllocationDaySummary
        {
            Day = FormatDay(day.Day),
            Status = AllocationDaySummary.StatusAllocated,
            Slots = day.Slots.Count,
            Consumers = ordered.Count(m => m.Kind == CommunityMemberKind.Consumer),
            Producers = ordered.Count(m => m.Kind == CommunityMemberKind.Producer),
            ConsumptionKWh = consumption,
            EligibleKWh = eligible,
            OfferedKWh = offeredKWh,
            AllocatedKWh = allocatedKWh,
            SurplusKWh = surplusKWh,
            MissingConsumerValues = missingConsumers,
            MissingProducerValues = missingProducers,
            Coverage = consumption > 0 ? Math.Round(allocatedKWh / consumption, 6) : null,
            SurplusRatio = offeredKWh > 0 ? Math.Round(surplusKWh / offeredKWh, 6) : null,
            CutOff = day.CutOffUtc
        };

        return (records, summary, negatives);
    }

    private static AllocationEnergyQuantity Quantity(CommunityMember m, AllocationSlot slot, decimal kWh, string quality)
        => new()
        {
            MeteringPointRtId = m.RtId,
            MeteringPointNumber = m.MeteringPointNumber,
            From = slot.From,
            To = slot.To,
            Quantity = kWh,
            Quality = quality
        };

    private static void AddRecord(List<AllocationEnergyData> records, AllocateCommunityEnergyNodeConfiguration c,
        AllocationDay day, CommunityMember m, string meterCode, List<AllocationEnergyQuantity> quantities)
    {
        // A day without a participating slot for this point produces no record (contract).
        if (quantities.Count == 0)
        {
            return;
        }

        records.Add(new AllocationEnergyData
        {
            MeteringPointRtId = m.RtId,
            MeteringPointNumber = m.MeteringPointNumber,
            MeterCode = meterCode,
            QuantityUnit = c.QuantityUnit,
            CreationTime = day.CutOffUtc,
            PeriodStart = day.StartUtc,
            PeriodEnd = day.EndUtc,
            EnergyQuantities = quantities
        });
    }

    // ==================================================================== loading

    private async Task<List<CommunityMember>> LoadMembersAsync(AllocateCommunityEnergyNodeConfiguration c,
        INodeContext nodeContext)
    {
        // AB#5028 / AB#5127 — scoped by default (config-selected identity): reads tenant business data
        // (metering points, participation periods, measurement anchors) only, like
        // SimulateEnergyMeasurements@1. One session for every read.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var members = await CommunityMemberLoader.LoadAsync(etlContext.TenantRepository, session,
            CommunityModelSchema.From(c), nodeContext, NodeName);
        await session.CommitTransactionAsync();
        return members;
    }

    // ==================================================================== archive

    private Task<Dictionary<(string, DateTime), CommunityRawValue>> ReadInputsAsync(
        AllocateCommunityEnergyNodeConfiguration c, INodeContext nodeContext,
        List<(AllocationDay Day, List<CommunityMember> Members, int SummaryIndex)> days, TimeSpan slotLength)
    {
        var anchorIds = days.SelectMany(d => d.Members)
            .Select(m => m.InputAnchorRtId)
            .OfType<string>()
            .ToList();

        // Valid slot starts per anchor: only slots of the requested days count.
        var slotStarts = days.SelectMany(d => d.Day.Slots).Select(s => s.From).ToHashSet();
        var from = days.Min(d => d.Day.StartUtc);
        var to = days.Max(d => d.Day.EndUtc);

        return CommunityInputReader.ReadAsync(systemContext, etlContext.TenantId, c.ArchiveRtId, c.ValueColumn,
            c.QualityColumn, anchorIds, slotStarts, from, to, slotLength, PageSize, nodeContext, NodeName);
    }

    // ==================================================================== value helpers

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ToDay(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    private static string? ResolveString(IDataContext dataContext, string? path)
        => string.IsNullOrWhiteSpace(path) ? null : dataContext.GetValue(path)?.ToString() is { Length: > 0 } s ? s : null;

    private static DateOnly? ResolveDay(IDataContext dataContext, INodeContext nodeContext, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = dataContext.GetValue(path);
        switch (value)
        {
            case null:
                return null;
            case DateTime dt:
                return DateOnly.FromDateTime(dt);
            case DateTimeOffset dto:
                return DateOnly.FromDateTime(dto.DateTime);
            case DateOnly d:
                return d;
            case string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day):
                return day;
            case string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var parsed):
                return DateOnly.FromDateTime(parsed.DateTime);
            case string s when string.IsNullOrWhiteSpace(s):
                return null;
            default:
                throw MeshAdapterPipelineExecutionException.InvalidDateTimeAtPath(nodeContext, path, value);
        }
    }
}

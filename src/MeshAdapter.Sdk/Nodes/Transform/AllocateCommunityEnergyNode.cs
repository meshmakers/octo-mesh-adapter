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

    private enum MemberKind
    {
        Consumer,
        Producer
    }

    private sealed record Member(
        string RtId,
        MemberKind Kind,
        string? MeteringPointNumber,
        int PartitionFactor,
        int DataSource,
        List<(DateTime? From, DateTime? To)> Periods)
    {
        public string? InputAnchorRtId { get; set; }

        public bool Participates(AllocationSlot slot)
            => Periods.Any(p => (p.From is null || p.From.Value <= slot.From)
                                && (p.To is null || slot.To <= p.To.Value));
    }

    private readonly record struct RawValue(decimal? Value, string Quality);

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<AllocateCommunityEnergyNodeConfiguration>();

        // ---------------------------------------------------------------- window
        var timeZoneId = ResolveString(dataContext, c.TimeZonePath) ?? c.TimeZone;
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {NodeName}: unknown time zone '{timeZoneId}'.", ex);
        }

        var cutOffTime = ResolveTimeOfDay(dataContext, nodeContext, c.CutOffTimePath) ?? c.CutOffTime;
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
        var daysToAllocate = new List<(AllocationDay Day, List<Member> Members, int SummaryIndex)>();
        foreach (var day in days)
        {
            var participating = members.Where(m => day.Slots.Any(m.Participates)).ToList();
            var consumers = participating.Count(m => m.Kind == MemberKind.Consumer);
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
            ? new Dictionary<(string, DateTime), RawValue>()
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
        List<Member> participating, IReadOnlyDictionary<(string, DateTime), RawValue> values)
    {
        // Deterministic member order: rtId ordinal. The allocator's tie breaker uses the same order.
        var ordered = participating.OrderBy(m => m.RtId, StringComparer.Ordinal).ToList();
        var share = ordered.ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var offered = ordered.Where(m => m.Kind == MemberKind.Producer)
            .ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var surplus = ordered.Where(m => m.Kind == MemberKind.Producer)
            .ToDictionary(m => m.RtId, _ => new List<AllocationEnergyQuantity>());
        var byRtId = ordered.ToDictionary(m => m.RtId);

        decimal consumption = 0, eligible = 0;
        long offeredUnits = 0, allocatedUnits = 0, surplusUnits = 0;
        int missingConsumers = 0, missingProducers = 0, negatives = 0;

        foreach (var slot in day.Slots)
        {
            var consumers = new List<AllocationMember>();
            var producers = new List<AllocationMember>();
            foreach (var m in ordered)
            {
                if (!m.Participates(slot))
                {
                    continue;
                }

                RawValue raw = default;
                var found = m.InputAnchorRtId is not null && values.TryGetValue((m.InputAnchorRtId, slot.From), out raw);
                var member = new AllocationMember(m.RtId, m.PartitionFactor, found ? raw.Value : null,
                    found ? raw.Quality : c.MissingQuality);
                (m.Kind == MemberKind.Consumer ? consumers : producers).Add(member);
            }

            if (consumers.Count == 0 && producers.Count == 0)
            {
                continue;
            }

            var result = allocator.Allocate(consumers, producers);
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
            if (m.Kind == MemberKind.Consumer)
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
            Consumers = ordered.Count(m => m.Kind == MemberKind.Consumer),
            Producers = ordered.Count(m => m.Kind == MemberKind.Producer),
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

    private static AllocationEnergyQuantity Quantity(Member m, AllocationSlot slot, decimal kWh, string quality)
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
        AllocationDay day, Member m, string meterCode, List<AllocationEnergyQuantity> quantities)
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

    private async Task<List<Member>> LoadMembersAsync(AllocateCommunityEnergyNodeConfiguration c,
        INodeContext nodeContext)
    {
        var consumerType = new RtCkId<CkTypeId>(c.ConsumerCkTypeId);
        var producerType = new RtCkId<CkTypeId>(c.ProducerCkTypeId);
        var meteringPointType = new RtCkId<CkTypeId>(c.MeteringPointCkTypeId);
        var periodType = new RtCkId<CkTypeId>(c.ParticipationPeriodCkTypeId);
        var periodRole = new RtCkId<CkAssociationRoleId>(c.ParticipationPeriodAssociationRoleId);
        var emType = new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId);
        var parentRole = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);

        // AB#5028 / AB#5127 — scoped by default (config-selected identity): reads tenant business data
        // (metering points, participation periods, measurement anchors) only, like
        // SimulateEnergyMeasurements@1. One session for every read.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();

        var consumers = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, consumerType, RtEntityQueryOptions.Create(), 0, int.MaxValue)).Items.ToList();
        var producers = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, producerType, RtEntityQueryOptions.Create(), 0, int.MaxValue)).Items.ToList();
        var periods = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, periodType, RtEntityQueryOptions.Create(), 0, int.MaxValue)).Items.ToList();

        IMultipleOriginResultSet<RtEntity>? periodTargets = null;
        if (periods.Count > 0)
        {
            periodTargets = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
                session, periods.Select(p => p.RtId).ToArray(), periodType, periodRole, meteringPointType,
                GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
        }

        var inputObis = new List<string> { c.ConsumerInputObis, c.ProducerInputObis };
        var anchors = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
                session, emType, RtEntityQueryOptions.Create().FieldIn(c.ObisCodeAttribute, inputObis),
                0, int.MaxValue)).Items
            .Where(a => inputObis.Contains(a.GetAttributeStringValueOrDefault(c.ObisCodeAttribute) ?? string.Empty,
                StringComparer.Ordinal))
            .ToList();

        IMultipleOriginResultSet<RtEntity>? anchorTargets = null;
        if (anchors.Count > 0)
        {
            // Outbound from each anchor via the parent role to its metering point; same 8-arg call
            // shape as SimulateEnergyMeasurements@1 and GetAssociationTargets@1.
            anchorTargets = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
                session, anchors.Select(a => a.RtId).ToArray(), emType, parentRole, meteringPointType,
                GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
        }

        await session.CommitTransactionAsync();

        // ---- participation periods by metering point
        var periodById = periods.ToDictionary(p => p.RtId.ToString(), StringComparer.Ordinal);
        var periodsByPoint = new Dictionary<string, List<(DateTime?, DateTime?)>>(StringComparer.Ordinal);
        var periodsWithoutRange = 0;
        foreach (var (origin, targets) in Pairs(periodTargets))
        {
            if (!periodById.TryGetValue(origin, out var period))
            {
                continue;
            }

            if (period.GetAttributeValueOrDefault(c.ParticipationTimeRangeAttribute) is not RtRecord range)
            {
                periodsWithoutRange++;
                continue;
            }

            var from = AsUtc(range.GetAttributeValueOrDefault("From"));
            var to = AsUtc(range.GetAttributeValueOrDefault("To"));
            foreach (var target in targets)
            {
                var key = target.RtId.ToString();
                if (!periodsByPoint.TryGetValue(key, out var list))
                {
                    periodsByPoint[key] = list = [];
                }

                list.Add((from, to));
            }
        }

        if (periodsWithoutRange > 0)
        {
            nodeContext.Warning(
                $"{NodeName}: {periodsWithoutRange} participation period(s) without '{c.ParticipationTimeRangeAttribute}' ignored.");
        }

        // ---- members
        var members = new Dictionary<string, Member>(StringComparer.Ordinal);
        var clampedFactors = 0;
        foreach (var (entities, kind) in new[] { (consumers, MemberKind.Consumer), (producers, MemberKind.Producer) })
        {
            foreach (var e in entities)
            {
                var rtId = e.RtId.ToString();
                if (members.ContainsKey(rtId))
                {
                    continue;
                }

                var pf = AsInt(e.GetAttributeValueOrDefault(c.PartitionFactorAttribute)) ?? c.DefaultPartitionFactor;
                if (pf is < 0 or > 100)
                {
                    clampedFactors++;
                    pf = Math.Clamp(pf, 0, 100);
                }

                var dataSource = AsDataSource(e.GetAttributeValueOrDefault(c.DataSourceAttribute)) ?? c.DefaultDataSource;
                members[rtId] = new Member(rtId, kind,
                    e.GetAttributeStringValueOrDefault(c.MeteringPointNumberAttribute),
                    pf, dataSource,
                    periodsByPoint.TryGetValue(rtId, out var list) ? list : []);
            }
        }

        if (clampedFactors > 0)
        {
            nodeContext.Warning($"{NodeName}: {clampedFactors} partition factor(s) outside 0..100 were clamped.");
        }

        // ---- input anchors by metering point and OBIS code
        var anchorById = anchors.ToDictionary(a => a.RtId.ToString(), StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var (origin, targets) in Pairs(anchorTargets).OrderBy(p => p.Origin, StringComparer.Ordinal))
        {
            if (!anchorById.TryGetValue(origin, out var anchor))
            {
                continue;
            }

            var obis = anchor.GetAttributeStringValueOrDefault(c.ObisCodeAttribute);
            foreach (var target in targets)
            {
                if (!members.TryGetValue(target.RtId.ToString(), out var member))
                {
                    continue;
                }

                var expected = member.Kind == MemberKind.Consumer ? c.ConsumerInputObis : c.ProducerInputObis;
                if (!string.Equals(obis, expected, StringComparison.Ordinal))
                {
                    continue;
                }

                // Origins are visited in rtId order, so the first anchor wins deterministically.
                if (member.InputAnchorRtId is null)
                {
                    member.InputAnchorRtId = origin;
                }
                else
                {
                    duplicates++;
                }
            }
        }

        if (duplicates > 0)
        {
            nodeContext.Warning(
                $"{NodeName}: {duplicates} additional input anchor(s) for the same metering point and register ignored.");
        }

        return members.Values.ToList();
    }

    private static IEnumerable<(string Origin, List<RtEntity> Targets)> Pairs(IMultipleOriginResultSet<RtEntity>? set)
    {
        if (set is null)
        {
            yield break;
        }

        foreach (var kvp in set)
        {
            yield return (kvp.Key.RtId.ToString(), kvp.Value.Items.ToList());
        }
    }

    // ==================================================================== archive

    private async Task<Dictionary<(string, DateTime), RawValue>> ReadInputsAsync(
        AllocateCommunityEnergyNodeConfiguration c, INodeContext nodeContext,
        List<(AllocationDay Day, List<Member> Members, int SummaryIndex)> days, TimeSpan slotLength)
    {
        var result = new Dictionary<(string, DateTime), RawValue>();
        var anchorIds = days.SelectMany(d => d.Members)
            .Select(m => m.InputAnchorRtId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new OctoObjectId(x))
            .ToList();
        if (anchorIds.Count == 0)
        {
            return result;
        }

        if (!OctoObjectId.TryParse(c.ArchiveRtId, out var archiveRtId))
        {
            throw MeshAdapterPipelineExecutionException.InvalidRtId(nodeContext, c.ArchiveRtId);
        }

        var tenantContext = await systemContext.FindTenantContextAsync(etlContext.TenantId);
        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext,
                             etlContext.TenantId);
        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                       ?? throw MeshAdapterPipelineExecutionException.ArchiveNotFound(nodeContext, archiveRtId);

        var resolver = StreamDataNodeHelpers.CreateFieldResolver(snapshot);
        var windowStart = StreamDataNodeHelpers.ResolveQueryableColumn("WindowStart", snapshot, resolver, nodeContext, "projection");
        var windowEnd = StreamDataNodeHelpers.ResolveQueryableColumn("WindowEnd", snapshot, resolver, nodeContext, "projection");
        var valueColumn = StreamDataNodeHelpers.ResolveQueryableColumn(c.ValueColumn, snapshot, resolver, nodeContext, "projection");
        var qualityColumn = StreamDataNodeHelpers.ResolveQueryableColumn(c.QualityColumn, snapshot, resolver, nodeContext, "projection");
        var rtIdColumn = StreamDataNodeHelpers.ResolveQueryableColumn("rtId", snapshot, resolver, nodeContext, "sorting");

        // Valid slot starts per anchor: only slots of the requested days count.
        var slotStarts = days.SelectMany(d => d.Day.Slots).Select(s => s.From).ToHashSet();
        var from = days.Min(d => d.Day.StartUtc);
        var to = days.Max(d => d.Day.EndUtc);

        var ignored = 0;
        var offset = 0;
        while (true)
        {
            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(snapshot.TargetCkTypeId)
                .WithColumns([windowStart.QueryName, windowEnd.QueryName, valueColumn.QueryName, qualityColumn.QueryName])
                .WithRtIds(anchorIds)
                .WithTimeRange(from, to)
                // A total order (window start, then rtId) keeps offset paging stable.
                .WithSortOrders([
                    new SortOrderItem(windowStart.QueryName, SortOrders.Ascending),
                    new SortOrderItem(rtIdColumn.QueryName, SortOrders.Ascending)
                ])
                .WithPagination(offset, PageSize);

            StreamDataQueryResult page;
            try
            {
                page = await repository.ExecuteQueryAsync(archiveRtId, options);
            }
            catch (Exception ex)
            {
                throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(nodeContext, archiveRtId, ex);
            }

            foreach (var row in page.Rows)
            {
                var start = AsUtc(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, windowStart.StorageKey));
                var end = AsUtc(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, windowEnd.StorageKey))
                          ?? AsUtc(row.Timestamp);
                if (row.RtId is null || start is null || end is null || end.Value - start.Value != slotLength
                    || !slotStarts.Contains(start.Value))
                {
                    ignored++;
                    continue;
                }

                var value = AsDecimal(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, valueColumn.StorageKey));
                var quality = QualityName(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, qualityColumn.StorageKey));
                // Rows arrive in a total order; the first row of a window wins.
                result.TryAdd((row.RtId.Value.ToString(), start.Value), new RawValue(value, quality));
            }

            offset += page.Rows.Count;
            if (page.Rows.Count < PageSize || page.Rows.Count == 0 || offset >= page.TotalCount)
            {
                break;
            }
        }

        if (ignored > 0)
        {
            nodeContext.Debug($"{NodeName}: {ignored} archive row(s) ignored because their window is not one slot of a requested day.");
        }

        nodeContext.Debug($"{NodeName}: read {offset} archive row(s) for {anchorIds.Count} input anchor(s).");
        return result;
    }

    // ==================================================================== value helpers

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quality keys 1, 2, 3, 4 become L1, L2, L3, Manual (Basic.Energy DataQuality); any other present
    /// value becomes L1. Manual is kept, not folded into L1, because the slot quality is the worst input
    /// quality (V-2) and a manual value must not pass as a measurement.
    /// </summary>
    internal static string QualityName(object? raw)
        => AsInt(raw) switch
        {
            2 => "L2",
            3 => "L3",
            4 => "Manual",
            _ => raw is string s && s is "L1" or "L2" or "L3" or "Manual" ? s : "L1"
        };

    internal static decimal? AsDecimal(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case decimal d:
                return d;
            case double d:
                return double.IsFinite(d) ? (decimal)d : null;
            case float f:
                return float.IsFinite(f) ? (decimal)f : null;
            case long l:
                return l;
            case int i:
                return i;
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case IConvertible convertible:
                try
                {
                    return convertible.ToDecimal(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    private static int? AsInt(object? raw)
        => raw switch
        {
            null => null,
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            short s => s,
            byte b => b,
            double d when double.IsFinite(d) && d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue => (int)d,
            decimal m when m % 1 == 0 && m is >= int.MinValue and <= int.MaxValue => (int)m,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
            _ => null
        };

    /// <summary>The data source enum key; tolerates the enum value names as well.</summary>
    private static int? AsDataSource(object? raw)
        => AsInt(raw) ?? (raw as string) switch
        {
            { } s when s.Equals("Eda", StringComparison.OrdinalIgnoreCase) => 0,
            { } s when s.Equals("Simulated", StringComparison.OrdinalIgnoreCase) => 1,
            { } s when s.Equals("SelfReported", StringComparison.OrdinalIgnoreCase) => 2,
            _ => null
        };

    private static DateTime? AsUtc(object? raw)
        => raw switch
        {
            DateTime dt => StreamDataNodeHelpers.ToUtc(dt),
            DateTimeOffset dto => dto.UtcDateTime,
            long ms => DateTime.UnixEpoch.AddMilliseconds(ms),
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null
        };

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

    private static TimeSpan? ResolveTimeOfDay(IDataContext dataContext, INodeContext nodeContext, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = dataContext.GetValue(path);
        return value switch
        {
            null => null,
            TimeSpan ts => ts,
            string s when string.IsNullOrWhiteSpace(s) => null,
            string s when TimeSpan.TryParseExact(s, ["hh\\:mm", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {NodeName}: '{path}' is not a time of day (HH:mm or HH:mm:ss).")
        };
    }
}

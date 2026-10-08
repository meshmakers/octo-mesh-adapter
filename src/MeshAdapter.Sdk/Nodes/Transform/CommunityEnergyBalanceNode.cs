using System.Globalization;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Provisional balance of an energy community for the last closed slots (AB#5639, BAL-01..03).
/// Loads the members, reads the raw registers and allocates every slot with exactly the code of
/// <c>AllocateCommunityEnergy@1</c> (<see cref="CommunityMemberLoader"/>,
/// <see cref="CommunityInputReader"/>, <see cref="CommunitySlotAllocation"/>), so a slot whose inputs
/// are complete shows the numbers the final run will write. Writes nothing.
/// </summary>
[NodeConfiguration(typeof(CommunityEnergyBalanceNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class CommunityEnergyBalanceNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext) : IPipelineNode
{
    private const string NodeName = "CommunityEnergyBalance";

    /// <summary>Clock deciding which slots are closed; replaced by tests.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Page size of the archive read; lowered by tests to exercise the paging.</summary>
    internal int PageSize { get; init; } = AllocateCommunityEnergyNode.ArchivePageSize;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CommunityEnergyBalanceNodeConfiguration>();
        var now = Clock.GetUtcNow().UtcDateTime;
        CommunityValues.ResolveTimeZone(c.TimeZone, nodeContext, NodeName);
        if (c.SlotLength <= TimeSpan.Zero)
        {
            throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {NodeName}: the slot length must be positive.");
        }

        // ---------------------------------------------------------------- request
        var (slotCount, slotError) = ResolveSlotCount(dataContext, c);
        if (slotError is not null)
        {
            await WriteAsync(dataContext, nodeContext, c, Error(now, slotError));
            return;
        }

        var filter = ResolveFilter(dataContext, c);
        var slots = LastClosedSlots(now, c.SlotLength, slotCount);

        // ---------------------------------------------------------------- runtime model
        // AB#5028 / AB#5127 — scoped by default (config-selected identity): reads tenant business data
        // (metering points, participation periods, measurement anchors) only, exactly like
        // AllocateCommunityEnergy@1, so both see the same members under the same identity.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var members = await CommunityMemberLoader.LoadAsync(etlContext.TenantRepository, session,
            CommunityModelSchema.From(c), nodeContext, NodeName);
        await session.CommitTransactionAsync();

        // Same deterministic member order as the final run (rtId ordinal).
        var participating = members
            .Where(m => slots.Any(m.Participates))
            .OrderBy(m => m.RtId, StringComparer.Ordinal)
            .ToList();

        var blocked = participating.Where(m => !c.AllocatableDataSources.Contains(m.DataSource)).ToList();
        if (blocked.Count > 0)
        {
            var names = string.Join(", ", blocked.Select(m => m.MeteringPointNumber ?? m.RtId));
            await WriteAsync(dataContext, nodeContext, c, Error(now,
                $"Participating metering point(s) with a data source that is not allocatable: {names}."));
            return;
        }

        // ---------------------------------------------------------------- archive
        var values = await CommunityInputReader.ReadAsync(systemContext, etlContext.TenantId, c.ArchiveRtId,
            c.ValueColumn, c.QualityColumn,
            participating.Select(m => m.InputAnchorRtId).OfType<string>().ToList(),
            slots.Select(s => s.From).ToHashSet(), slots[0].From, slots[^1].To, c.SlotLength, PageSize,
            nodeContext, NodeName);

        // ---------------------------------------------------------------- allocation, slot by slot
        var allocator = new CommunityAllocator(c.Resolution, c.MissingQuality);
        var simulatedWithValue = participating
            .Where(m => m.DataSource == c.SimulatedDataSource)
            .ToDictionary(m => m.RtId, _ => false, StringComparer.Ordinal);
        var detail = filter is null
            ? []
            : participating.Where(m => m.MeteringPointNumber is not null && filter.Contains(m.MeteringPointNumber))
                .OrderBy(m => m.MeteringPointNumber, StringComparer.Ordinal)
                .ThenBy(m => m.Kind)
                .ToList();

        var slotArray = new JsonArray();
        foreach (var slot in slots)
        {
            var result = CommunitySlotAllocation.Allocate(allocator, slot, participating, values, c.MissingQuality,
                out var consumers, out var producers);

            decimal production = 0;
            var received = 0;
            var simulated = 0;
            foreach (var m in consumers.Concat(producers))
            {
                if (m.Value is null)
                {
                    continue;
                }

                received++;
                if (simulatedWithValue.ContainsKey(m.RtId))
                {
                    simulated++;
                    simulatedWithValue[m.RtId] = true;
                }
            }

            foreach (var p in producers)
            {
                production += p.Value is > 0 ? p.Value.Value : 0;
            }

            var consumption = result?.ConsumptionKWh ?? 0;
            var offered = allocator.ToKWh(result?.OfferedUnits ?? 0);
            var allocated = allocator.ToKWh(result?.AllocatedUnits ?? 0);
            var surplus = allocator.ToKWh(result?.SurplusUnits ?? 0);

            var slotNode = new JsonObject
            {
                ["from"] = Iso(slot.From),
                ["to"] = Iso(slot.To),
                ["productionKwh"] = production,
                ["offeredKwh"] = offered,
                ["allocatedKwh"] = allocated,
                ["surplusKwh"] = surplus,
                ["consumptionKwh"] = consumption,
                ["shortfallKwh"] = consumption - allocated,
                ["coverage"] = consumption > 0 ? Math.Round(allocated / consumption, 3) : 0m,
                ["reported"] = new JsonObject
                {
                    ["expected"] = consumers.Count + producers.Count,
                    ["received"] = received,
                    ["simulated"] = simulated
                }
            };

            if (filter is not null)
            {
                slotNode["meteringPoints"] = MeteringPointDetail(c, allocator, slot, detail, consumers, producers,
                    result);
            }

            slotArray.Add(slotNode);
        }

        var simulatedIncluded = simulatedWithValue.Values.All(v => v);
        var output = new JsonObject
        {
            ["status"] = "ok",
            ["message"] = $"Provisional balance of {slots.Count} closed slot(s).",
            ["generatedAt"] = Iso(now),
            ["provisional"] = true,
            ["simulatedIncluded"] = simulatedIncluded,
            ["slots"] = slotArray
        };

        nodeContext.Debug($"{NodeName}: {slots.Count} slot(s), {participating.Count} participating metering point(s).");
        await WriteAsync(dataContext, nodeContext, c, output);
    }

    private static JsonArray MeteringPointDetail(CommunityEnergyBalanceNodeConfiguration c,
        CommunityAllocator allocator, AllocationSlot slot, List<CommunityMember> detail,
        List<AllocationMember> consumers,
        List<AllocationMember> producers, SlotAllocation? result)
    {
        var array = new JsonArray();
        foreach (var m in detail)
        {
            if (!m.Participates(slot))
            {
                continue;
            }

            var registers = new JsonObject();
            if (m.Kind == CommunityMemberKind.Consumer)
            {
                var index = consumers.FindIndex(x => x.RtId == m.RtId);
                registers[c.ConsumerInputObis] = index >= 0 ? JsonValueOf(consumers[index].Value) : null;
                registers[c.ConsumerShareObis] = index >= 0 && result is not null
                    ? allocator.ToKWh(result.Consumers[index].ShareUnits)
                    : 0m;
            }
            else
            {
                var index = producers.FindIndex(x => x.RtId == m.RtId);
                registers[c.ProducerInputObis] = index >= 0 ? JsonValueOf(producers[index].Value) : null;
                registers[c.ProducerOfferedObis] = index >= 0 && result is not null
                    ? allocator.ToKWh(result.Producers[index].OfferedUnits)
                    : 0m;
                registers[c.ProducerSurplusObis] = index >= 0 && result is not null
                    ? allocator.ToKWh(result.Producers[index].SurplusUnits)
                    : 0m;
            }

            array.Add(new JsonObject
            {
                ["meteringPointNumber"] = m.MeteringPointNumber,
                ["direction"] = m.Kind == CommunityMemberKind.Consumer ? "consumption" : "production",
                ["registers"] = registers
            });
        }

        return array;
    }

    private static JsonNode? JsonValueOf(decimal? value) => value is { } v ? JsonValue.Create(v) : null;

    /// <summary>The last <paramref name="count"/> slots on the UTC grid whose end is not after now, oldest first.</summary>
    internal static IReadOnlyList<AllocationSlot> LastClosedSlots(DateTime now, TimeSpan slotLength, int count)
    {
        var utc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var lastEnd = new DateTime(utc.Ticks - utc.Ticks % slotLength.Ticks, DateTimeKind.Utc);
        var slots = new AllocationSlot[count];
        for (var i = 0; i < count; i++)
        {
            var from = lastEnd.AddTicks(-slotLength.Ticks * (count - i));
            slots[i] = new AllocationSlot(from, from.Add(slotLength));
        }

        return slots;
    }

    private static (int Count, string? Error) ResolveSlotCount(IDataContext dataContext,
        CommunityEnergyBalanceNodeConfiguration c)
    {
        var count = c.Slots;
        if (!string.IsNullOrWhiteSpace(c.SlotsPath))
        {
            var raw = dataContext.GetValue(c.SlotsPath);
            if (raw is not null && !(raw is string s && string.IsNullOrWhiteSpace(s)))
            {
                if (CommunityValues.AsInt(raw) is not { } parsed)
                {
                    return (0, "slots must be a whole number.");
                }

                count = parsed;
            }
        }

        return count < 1 || count > c.MaxSlots
            ? (0, $"slots must be between 1 and {c.MaxSlots} (got {count}).")
            : (count, null);
    }

    private static HashSet<string>? ResolveFilter(IDataContext dataContext, CommunityEnergyBalanceNodeConfiguration c)
    {
        if (string.IsNullOrWhiteSpace(c.MeteringPointsPath)
            || dataContext.Get<JsonNode>(c.MeteringPointsPath) is not JsonArray array)
        {
            return null;
        }

        var numbers = array
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
            .OfType<string>()
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return numbers.Count == 0 ? null : numbers;
    }

    private static JsonObject Error(DateTime now, string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
        ["generatedAt"] = Iso(now),
        ["provisional"] = true,
        ["simulatedIncluded"] = false,
        ["slots"] = new JsonArray()
    };

    private async Task WriteAsync(IDataContext dataContext, INodeContext nodeContext,
        CommunityEnergyBalanceNodeConfiguration c, JsonObject output)
    {
        dataContext.Set<JsonNode>(c.TargetPath, output, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        await next(dataContext, nodeContext);
    }

    internal static string Iso(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

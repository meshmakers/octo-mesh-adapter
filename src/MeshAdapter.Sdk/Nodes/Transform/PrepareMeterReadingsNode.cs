using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Validates readings reported for self-reported metering points (AB#5637, MTR-02..04) and shapes the
/// accepted values into the records <c>SaveTimeRangeSeriesInArchive@1</c> writes with the column
/// block of the allocation pipeline. Partial acceptance: every value is judged on its own, and the
/// result lists each rejected value with its position and a code. Writes nothing itself.
/// </summary>
/// <remarks>
/// The reporting cut-off is computed by <see cref="AllocationDayPlanner"/> with the same settings
/// semantics as <c>AllocateCommunityEnergy@1</c>, so a day is closed for reporting exactly when the
/// final allocation may run for it. Neither the request nor any value is logged.
/// </remarks>
[NodeConfiguration(typeof(PrepareMeterReadingsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class PrepareMeterReadingsNode(
    NodeDelegate next,
    IMeshEtlContext etlContext) : IPipelineNode
{
    private const string NodeName = "PrepareMeterReadings";

    internal const string UnknownMeteringPoint = "UNKNOWN_METERING_POINT";
    internal const string NotSelfReported = "NOT_SELF_REPORTED";
    internal const string OffGrid = "OFF_GRID";
    internal const string DuplicateSlot = "DUPLICATE_SLOT";
    internal const string OutOfRange = "OUT_OF_RANGE";
    internal const string InFuture = "IN_FUTURE";
    internal const string NotParticipating = "NOT_PARTICIPATING";
    internal const string DayFrozen = "DAY_FROZEN";

    /// <summary>Clock for "now" (future check, cut-off, creation time); replaced by tests.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    private sealed class Entry
    {
        public required int Reading { get; init; }
        public required int Value { get; init; }
        public CommunityMember? Member { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public decimal KWh { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }

        public void Reject(string code, string message)
        {
            Code = code;
            Message = message;
        }
    }

    private sealed record Reading(int Index, string? Number, CommunityMemberKind? Kind, string? Direction, JsonArray Values);

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<PrepareMeterReadingsNodeConfiguration>();
        var now = Clock.GetUtcNow().UtcDateTime;

        var timeZone = CommunityValues.ResolveTimeZone(c.TimeZone, nodeContext, NodeName);
        var cutOffTime = CommunityValues.ResolveTimeOfDay(dataContext, nodeContext, c.CutOffTimePath, NodeName)
                         ?? c.CutOffTime;
        var planner = new AllocationDayPlanner(timeZone, c.CutOffDayOffset, cutOffTime, c.SlotLength);
        var frozenUntil = planner.BuildDay(planner.LatestClosedDay(now)).EndUtc;

        // ---------------------------------------------------------------- request shape
        var readings = ParseReadings(dataContext.Get<JsonNode>(c.ReadingsPath));
        var total = readings?.Sum(r => r.Values.Count) ?? 0;
        if (readings is null || total == 0)
        {
            await WriteAsync(dataContext, nodeContext, c, [],
                RequestError("The request contains no readings with values.", frozenUntil));
            return;
        }

        if (total > c.MaxValues)
        {
            await WriteAsync(dataContext, nodeContext, c, [],
                RequestError($"The request contains {total} values; at most {c.MaxValues} are allowed per call.",
                    frozenUntil));
            return;
        }

        // ---------------------------------------------------------------- metering points
        var numbers = readings.Select(r => r.Number).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var members = new Dictionary<(string, CommunityMemberKind), CommunityMember>();
        if (numbers.Count > 0)
        {
            // AB#5028 / AB#5127 — scoped by default (config-selected identity): reads the tenant's own
            // metering points and participation periods. A narrower identity that cannot see a metering
            // point answers UNKNOWN_METERING_POINT, which is the correct answer for that caller.
            var session = await etlContext.GetSessionForAsync(c.Identity);
            session.StartTransaction();
            var loaded = await CommunityMemberLoader.LoadAsync(etlContext.TenantRepository, session,
                CommunityModelSchema.From(c), nodeContext, NodeName, loadAnchors: false,
                meteringPointNumbers: numbers);
            await session.CommitTransactionAsync();

            foreach (var m in loaded.OrderBy(m => m.RtId, StringComparer.Ordinal))
            {
                if (m.MeteringPointNumber is not null)
                {
                    members.TryAdd((m.MeteringPointNumber, m.Kind), m);
                }
            }
        }

        // ---------------------------------------------------------------- pass 1: point, shape, grid
        var entries = new List<Entry>(total);
        foreach (var reading in readings)
        {
            CommunityMember? member = null;
            string? readingCode = null, readingMessage = null;
            if (reading.Number is null)
            {
                (readingCode, readingMessage) = (UnknownMeteringPoint, "meteringPointNumber is missing.");
            }
            else if (reading.Kind is null)
            {
                (readingCode, readingMessage) = (UnknownMeteringPoint,
                    "direction must be 'consumption' or 'production'.");
            }
            else if (!members.TryGetValue((reading.Number, reading.Kind.Value), out member))
            {
                (readingCode, readingMessage) = (UnknownMeteringPoint,
                    $"Metering point {reading.Number} with direction {reading.Direction} is unknown.");
            }
            else if (member.DataSource != c.SelfReportedDataSource)
            {
                (readingCode, readingMessage) = (NotSelfReported,
                    $"Metering point {reading.Number} is not self-reported; its values cannot be reported.");
            }

            for (var v = 0; v < reading.Values.Count; v++)
            {
                var entry = new Entry { Reading = reading.Index, Value = v, Member = member };
                entries.Add(entry);
                if (readingCode is not null)
                {
                    entry.Reject(readingCode, readingMessage!);
                    continue;
                }

                var value = reading.Values[v] as JsonObject;
                var from = ReadInstant(value?["from"]);
                var to = ReadInstant(value?["to"]);
                if (from is null || to is null)
                {
                    entry.Reject(OffGrid, "from and to must be ISO-8601 instants.");
                    continue;
                }

                entry.From = from.Value;
                entry.To = to.Value;
                if (from.Value.Ticks % c.SlotLength.Ticks != 0 || to.Value - from.Value != c.SlotLength)
                {
                    entry.Reject(OffGrid,
                        $"The value must start on the {c.SlotLength.TotalMinutes:0}-minute grid and last exactly " +
                        $"{c.SlotLength.TotalMinutes:0} minutes.");
                    continue;
                }

                var kWh = ReadDecimal(value?["kWh"]);
                if (kWh is null || kWh.Value < 0 || kWh.Value > c.MaxKWh)
                {
                    // Range is checked after the duplicate rule; remember the problem for pass 3.
                    entry.KWh = -1;
                    entry.Message = kWh is null ? "kWh must be a number." : null;
                }
                else
                {
                    entry.KWh = kWh.Value;
                }
            }
        }

        // ---------------------------------------------------------------- pass 2: duplicates
        foreach (var group in entries.Where(e => e.Code is null)
                     .GroupBy(e => (e.Member!.RtId, e.From))
                     .Where(g => g.Count() > 1))
        {
            foreach (var entry in group)
            {
                entry.Reject(DuplicateSlot,
                    $"The slot starting {Iso(entry.From)} is reported more than once for this metering point.");
            }
        }

        // ---------------------------------------------------------------- pass 3: value, time, participation, cut-off
        var maxKWh = c.MaxKWh.ToString(CultureInfo.InvariantCulture);
        foreach (var entry in entries.Where(e => e.Code is null))
        {
            if (entry.KWh < 0)
            {
                entry.Reject(OutOfRange, entry.Message ?? $"kWh must be between 0 and {maxKWh}.");
                continue;
            }

            if (entry.To > now + c.FutureTolerance)
            {
                entry.Reject(InFuture, $"The slot ending {Iso(entry.To)} lies in the future.");
                continue;
            }

            if (!entry.Member!.Participates(new AllocationSlot(entry.From, entry.To)))
            {
                entry.Reject(NotParticipating,
                    $"The metering point does not participate in the slot starting {Iso(entry.From)}.");
                continue;
            }

            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(entry.From, timeZone));
            var cutOff = planner.CutOffUtc(day);
            if (now >= cutOff)
            {
                entry.Reject(DayFrozen,
                    $"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} is closed since {Iso(cutOff)}.");
            }
        }

        // ---------------------------------------------------------------- records and result
        var accepted = entries.Where(e => e.Code is null).ToList();
        var records = accepted
            .GroupBy(e => e.Member!.RtId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var member = g.First().Member!;
                var quantities = g.OrderBy(e => e.From)
                    .Select(e => new AllocationEnergyQuantity
                    {
                        MeteringPointRtId = member.RtId,
                        MeteringPointNumber = member.MeteringPointNumber,
                        From = e.From,
                        To = e.To,
                        Quantity = e.KWh,
                        Quality = c.Quality
                    })
                    .ToList();
                return new AllocationEnergyData
                {
                    MeteringPointRtId = member.RtId,
                    MeteringPointNumber = member.MeteringPointNumber,
                    MeterCode = member.Kind == CommunityMemberKind.Consumer ? c.ConsumerInputObis : c.ProducerInputObis,
                    QuantityUnit = c.QuantityUnit,
                    CreationTime = now,
                    PeriodStart = quantities[0].From,
                    PeriodEnd = quantities[^1].To,
                    EnergyQuantities = quantities
                };
            })
            .ToList();

        var rejected = new JsonArray();
        foreach (var e in entries.Where(e => e.Code is not null))
        {
            rejected.Add(new JsonObject
            {
                ["reading"] = e.Reading,
                ["value"] = e.Value,
                ["code"] = e.Code,
                ["message"] = e.Message
            });
        }

        var status = accepted.Count == 0 ? "error" : rejected.Count > 0 ? "partial" : "ok";
        var result = new JsonObject
        {
            ["status"] = status,
            ["message"] = $"{accepted.Count} of {entries.Count} values accepted.",
            ["accepted"] = accepted.Count,
            ["rejected"] = rejected,
            ["frozenUntil"] = Iso(frozenUntil)
        };

        nodeContext.Debug($"{NodeName}: {accepted.Count} of {entries.Count} value(s) accepted for " +
                          $"{records.Count} metering point(s).");
        await WriteAsync(dataContext, nodeContext, c, records, result);
    }

    private static List<Reading>? ParseReadings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var readings = new List<Reading>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var obj = array[i] as JsonObject;
            var number = ReadString(obj?["meteringPointNumber"]);
            var direction = ReadString(obj?["direction"]);
            CommunityMemberKind? kind = direction?.ToLowerInvariant() switch
            {
                "consumption" => CommunityMemberKind.Consumer,
                "production" => CommunityMemberKind.Producer,
                _ => null
            };
            readings.Add(new Reading(i, number, kind, direction, obj?["values"] as JsonArray ?? []));
        }

        return readings;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    internal static DateTime? ReadInstant(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<DateTimeOffset>(out var dto) && v.GetValueKind() != JsonValueKind.String)
        {
            return dto.UtcDateTime;
        }

        if (v.TryGetValue<DateTime>(out var dt) && v.GetValueKind() != JsonValueKind.String)
        {
            return DateTime.SpecifyKind(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt, DateTimeKind.Utc);
        }

        return v.TryGetValue<string>(out var s)
               && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    internal static decimal? ReadDecimal(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.GetValueKind() == JsonValueKind.Number)
        {
            // The raw JSON token, so a value from an element and one built in code read the same.
            return CommunityValues.ParseJsonNumber(v);
        }

        return v.TryGetValue<string>(out var s)
               && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static JsonObject RequestError(string message, DateTime frozenUntil) => new()
    {
        ["status"] = "error",
        ["code"] = "VALIDATION",
        ["message"] = message,
        ["accepted"] = 0,
        ["rejected"] = new JsonArray(),
        ["frozenUntil"] = Iso(frozenUntil)
    };

    private async Task WriteAsync(IDataContext dataContext, INodeContext nodeContext,
        PrepareMeterReadingsNodeConfiguration c, List<AllocationEnergyData> records, JsonObject result)
    {
        dataContext.Set(c.TargetPath, records, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        dataContext.Set<JsonNode>(c.ResultTargetPath, result, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        await next(dataContext, nodeContext);
    }

    private static string Iso(DateTime utc) => CommunityEnergyBalanceNode.Iso(utc);
}

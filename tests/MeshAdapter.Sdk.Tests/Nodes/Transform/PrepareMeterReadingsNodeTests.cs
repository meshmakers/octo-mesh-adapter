using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// PrepareMeterReadings@1 (AB#5637, MTR-02..04): every per-value code, partial acceptance, the
/// cut-off (incl. its path override), the request limits and the record shape that
/// SaveTimeRangeSeriesInArchive@1 consumes with the allocation pipeline's column block.
/// </summary>
public class PrepareMeterReadingsNodeTests : CommunityNodeTestBase
{
    private const string RecordsPath = "$.readings.records";
    private const string ResultPath = "$.readings.result";
    private const string C1 = "0000000000000000000000c1";
    private const string P1 = "0000000000000000000000a1";
    private const string S1 = "0000000000000000000000b1";
    private const string NC1 = "AT0099980000000000000000000000C01";
    private const string NP1 = "AT0099980000000000000000000000P01";
    private const string NS1 = "AT0099990000000000000000000000S01";

    /// <summary>Now: 2026-11-15 10:07 in Vienna; 2026-11-14 is closed since 2026-11-15T05:00Z.</summary>
    private static readonly DateTime Now = new(2026, 11, 15, 9, 7, 0, DateTimeKind.Utc);

    public PrepareMeterReadingsNodeTests()
    {
        AddConsumer(C1, NC1, dataSource: 2, from: new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc));
        AddProducer(P1, NP1, dataSource: 2);
        AddConsumer(S1, NS1, dataSource: 1);
    }

    private static PrepareMeterReadingsNodeConfiguration Config(string? cutOffTimePath = null, int? maxValues = null)
        => new()
        {
            TargetPath = RecordsPath,
            ResultTargetPath = ResultPath,
            CutOffTimePath = cutOffTimePath,
            MaxValues = maxValues ?? 2880
        };

    private sealed class Output
    {
        public List<AllocationEnergyData> Records { get; set; } = null!;
        public JsonObject Result { get; set; } = null!;
        public JsonArray Rejected => Result["rejected"]!.AsArray();
        public string Status => Result["status"]!.GetValue<string>();
        public int Accepted => Result["accepted"]!.GetValue<int>();
        public string Code(int i) => Rejected[i]!["code"]!.GetValue<string>();
    }

    private async Task<Output> RunAsync(JsonNode? readings, PrepareMeterReadingsNodeConfiguration? config = null,
        DateTime? now = null, Action<IDataContext>? setup = null)
    {
        config ??= Config();
        var (dataContext, nodeContext, next, _) = PrepareTestWithLogger(config);
        A.CallTo(() => dataContext.Get<JsonNode>(config.ReadingsPath)).Returns(readings);
        setup?.Invoke(dataContext);

        var output = new Output();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                switch ((string)call.Arguments[0]!)
                {
                    case RecordsPath: output.Records = (List<AllocationEnergyData>)call.Arguments[1]!; break;
                    case ResultPath: output.Result = (JsonObject)call.Arguments[1]!; break;
                }
            });

        var node = new PrepareMeterReadingsNode(next, EtlContext)
        {
            Clock = new FakeTimeProvider(new DateTimeOffset(now ?? Now, TimeSpan.Zero))
        };
        await node.ProcessObjectAsync(dataContext, nodeContext);
        VerifyNextCalled(next, dataContext, nodeContext);
        return output;
    }

    private static JsonObject Value(DateTime from, object? kWh, DateTime? to = null) => new()
    {
        ["from"] = from.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["to"] = (to ?? from.AddMinutes(15)).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["kWh"] = JsonValue.Create(kWh)
    };

    private static JsonObject Reading(string number, string direction, params JsonObject[] values) => new()
    {
        ["meteringPointNumber"] = number,
        ["direction"] = direction,
        ["values"] = new JsonArray(values.Cast<JsonNode?>().ToArray())
    };

    private static readonly DateTime Eight = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);

    // ==================================================================== acceptance

    [Fact]
    public async Task ValidValues_BecomeOneRecordPerMeteringPoint()
    {
        var output = await RunAsync(new JsonArray(
            Reading(NC1, "consumption", Value(Eight.AddMinutes(15), 0.875m), Value(Eight, 1.25m)),
            Reading(NP1, "production", Value(Eight, 2))));

        Assert.Equal("ok", output.Status);
        Assert.Equal("3 of 3 values accepted.", output.Result["message"]!.GetValue<string>());
        Assert.Equal(3, output.Accepted);
        Assert.Empty(output.Rejected);
        Assert.Equal("2026-11-14T23:00:00Z", output.Result["frozenUntil"]!.GetValue<string>());

        Assert.Equal([(P1, ProducerIn), (C1, ConsumerIn)],
            output.Records.Select(r => (r.MeteringPointRtId, r.MeterCode)));
        var consumer = output.Records[1];
        Assert.Equal(NC1, consumer.MeteringPointNumber);
        Assert.Equal("kWh", consumer.QuantityUnit);
        Assert.Equal(Now, consumer.CreationTime);
        Assert.Equal(Eight, consumer.PeriodStart);
        Assert.Equal(Eight.AddMinutes(30), consumer.PeriodEnd);
        Assert.Equal([1.25m, 0.875m], consumer.EnergyQuantities.Select(q => q.Quantity));
        Assert.All(consumer.EnergyQuantities, q => Assert.Equal("L1", q.Quality));
        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task ParsedRequest_FromTheApiSketch_IsAccepted()
    {
        // The request as the HTTP trigger hands it on: parsed JSON (element-backed values).
        var body = JsonNode.Parse($$"""
            { "readings": [ { "meteringPointNumber": "{{NC1}}", "direction": "consumption",
              "values": [ { "from": "2026-11-15T08:00:00Z", "to": "2026-11-15T08:15:00Z", "kWh": 1.250 },
                          { "from": "2026-11-15T08:15:00Z", "to": "2026-11-15T08:30:00Z", "kWh": 1 } ] } ] }
            """)!;
        var output = await RunAsync(body["readings"]);

        Assert.Equal("ok", output.Status);
        Assert.Equal([1.250m, 1m], output.Records.Single().EnergyQuantities.Select(q => q.Quantity));
    }

    [Fact]
    public async Task Records_AreConsumedBySaveTimeRangeSeriesInArchiveWithTheAllocationColumnBlock()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption", Value(Eight, 1.25m))));

        // The column block of allocate-community-energy / handle-daten-crmsg.yaml.
        var save = new SaveTimeRangeSeriesInArchiveNodeConfiguration
        {
            Path = RecordsPath,
            ArchiveRtId = ArchiveRtId.ToString(),
            CkTypeId = EmType,
            ValuesProperty = "EnergyQuantities",
            WellKnownNameFormat = "{MeteringPointRtId}_{MeterCode}",
            FromProperty = "From",
            ToProperty = "To",
            AnchorWindowFromAttribute = "TimeRange.From",
            AnchorWindowToAttribute = "TimeRange.To",
            ParentRtIdProperty = "MeteringPointRtId",
            ParentCkTypeId = "Basic.Energy/MeteringPoint",
            ParentAssociationRoleId = "System/ParentChild",
            Columns =
            [
                new TimeRangeSeriesColumn { Name = "Amount.Value", ValueProperty = "Quantity" },
                new TimeRangeSeriesColumn { Name = "Amount.Unit", ValueProperty = "QuantityUnit", Scope = TimeRangeSeriesColumnScope.Series },
                new TimeRangeSeriesColumn { Name = "DataQuality", ValueProperty = "Quality" },
                new TimeRangeSeriesColumn { Name = "ObisCode", ValueProperty = "MeterCode", Scope = TimeRangeSeriesColumnScope.Series },
                new TimeRangeSeriesColumn { Name = "SourceDocumentDate", ValueProperty = "CreationTime", Scope = TimeRangeSeriesColumnScope.Series }
            ]
        };

        var json = (JsonArray)JsonSerializer.SerializeToNode(output.Records, SystemTextJsonOptions.NodeNavigation)!;
        var series = TimeRangeSeriesShaper.Shape(json, save, out var unresolved);
        Assert.Equal(0, unresolved);
        Assert.Equal([$"{C1}_{ConsumerIn}"], series.Select(s => s.WellKnownName));
        series[0].RtId = OctoObjectId.GenerateNewId();

        var rows = TimeRangeSeriesShaper.BuildRows(series, new RtCkId<CkTypeId>(EmType), save, (_, raw) => raw,
            out var skipped);
        Assert.Equal(0, skipped);
        var row = Assert.Single(rows);
        Assert.Equal(Eight, row.From);
        Assert.Equal(Eight.AddMinutes(15), row.To);
        Assert.Equal(1.25m, Convert.ToDecimal(row.Attributes["Amount.Value"]));
        Assert.Equal("kWh", row.Attributes["Amount.Unit"]);
        Assert.Equal("L1", row.Attributes["DataQuality"]);
        Assert.Equal(ConsumerIn, row.Attributes["ObisCode"]);
        Assert.Equal(Now, row.Attributes["SourceDocumentDate"]);
    }

    [Fact]
    public async Task ThreeValidAndOneInvalid_IsPartialWithThePositionOfTheInvalidOne()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(Eight, 1), Value(Eight.AddMinutes(15), 1), Value(Eight.AddMinutes(30), 1),
            Value(Eight.AddMinutes(45), -1))));

        Assert.Equal("partial", output.Status);
        Assert.Equal("3 of 4 values accepted.", output.Result["message"]!.GetValue<string>());
        Assert.Equal(3, output.Accepted);
        var rejected = Assert.Single(output.Rejected)!;
        Assert.Equal(0, rejected["reading"]!.GetValue<int>());
        Assert.Equal(3, rejected["value"]!.GetValue<int>());
        Assert.Equal(PrepareMeterReadingsNode.OutOfRange, rejected["code"]!.GetValue<string>());
        Assert.Equal(3, output.Records.Single().EnergyQuantities.Count);
    }

    // ==================================================================== codes

    [Fact]
    public async Task UnknownNumberOrDirection_IsUnknownMeteringPoint()
    {
        var output = await RunAsync(new JsonArray(
            Reading("AT0099980000000000000000000000X99", "consumption", Value(Eight, 1)),
            Reading(NC1, "production", Value(Eight, 1)),
            Reading(NC1, "sideways", Value(Eight, 1))));

        Assert.Equal("error", output.Status);
        Assert.Equal(0, output.Accepted);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(PrepareMeterReadingsNode.UnknownMeteringPoint, output.Code(i)));
        Assert.Empty(output.Records);
    }

    [Fact]
    public async Task SimulatedMeteringPoint_IsNotSelfReported()
    {
        var output = await RunAsync(new JsonArray(Reading(NS1, "consumption", Value(Eight, 1))));
        Assert.Equal(PrepareMeterReadingsNode.NotSelfReported, output.Code(0));
    }

    [Fact]
    public async Task OffGrid_WrongStartOrLengthOrUnparsable()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(Eight.AddMinutes(5), 1),
            Value(Eight, 1, Eight.AddMinutes(30)),
            Value(Eight.AddSeconds(1), 1),
            new JsonObject { ["from"] = "yesterday", ["to"] = "today", ["kWh"] = 1 })));

        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(PrepareMeterReadingsNode.OffGrid, output.Code(i)));
    }

    [Fact]
    public async Task OutOfRange_NegativeTooLargeOrNotANumber()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(Eight, -0.001m), Value(Eight.AddMinutes(15), 1000.001m), Value(Eight.AddMinutes(30), "abc"),
            Value(Eight.AddMinutes(45), 1000))));

        Assert.Equal([PrepareMeterReadingsNode.OutOfRange, PrepareMeterReadingsNode.OutOfRange,
            PrepareMeterReadingsNode.OutOfRange], output.Rejected.Select(r => r!["code"]!.GetValue<string>()));
        Assert.Equal(1, output.Accepted);
    }

    [Fact]
    public async Task InFuture_BeyondTheTolerance()
    {
        // Now is 09:07Z: 09:00-09:15 ends 8 min in the future (rejected), 08:45-09:00 is fine.
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(Eight.AddMinutes(45), 1), Value(Eight.AddMinutes(60), 1))));
        Assert.Equal(1, output.Accepted);
        Assert.Equal(PrepareMeterReadingsNode.InFuture, output.Code(0));
        Assert.Equal(1, output.Rejected[0]!["value"]!.GetValue<int>());

        // 08:45-09:00 with now = 08:59 lies within the one-minute tolerance.
        var early = await RunAsync(new JsonArray(Reading(NC1, "consumption", Value(Eight.AddMinutes(45), 1))),
            now: Eight.AddMinutes(59));
        Assert.Equal("ok", early.Status);
    }

    [Fact]
    public async Task BeforeTheParticipationStart_IsNotParticipating()
    {
        // C1 participates from 2026-11-01T23:00Z.
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(new DateTime(2026, 11, 1, 22, 45, 0, DateTimeKind.Utc), 1))),
            now: new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc));
        Assert.Equal(PrepareMeterReadingsNode.NotParticipating, output.Code(0));
    }

    [Fact]
    public async Task ClosedDay_IsDayFrozenWithItsCutOff()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), 1),
            // 2026-11-14T23:00Z is already 2026-11-15 in Vienna: open.
            Value(new DateTime(2026, 11, 14, 23, 0, 0, DateTimeKind.Utc), 1))));

        Assert.Equal("partial", output.Status);
        Assert.Equal(PrepareMeterReadingsNode.DayFrozen, output.Code(0));
        Assert.Equal("2026-11-14 is closed since 2026-11-15T05:00:00Z.",
            output.Rejected[0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task CutOffTimePath_MovesTheCutOff()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
                Value(new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), 1))),
            Config("$.settings.cutOff"),
            setup: dc => A.CallTo(() => dc.GetValue("$.settings.cutOff", A<bool>._)).Returns("10:30"));

        // 2026-11-14 now closes at 2026-11-15 10:30 Vienna = 09:30Z, after now.
        Assert.Equal("ok", output.Status);
        Assert.Equal("2026-11-13T23:00:00Z", output.Result["frozenUntil"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyCutOffTime_KeepsTheConfiguredOne()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
                Value(new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), 1))),
            Config("$.settings.cutOff"),
            setup: dc => A.CallTo(() => dc.GetValue("$.settings.cutOff", A<bool>._)).Returns(""));
        Assert.Equal(PrepareMeterReadingsNode.DayFrozen, output.Code(0));
    }

    [Fact]
    public async Task SameSlotTwice_IsRejectedAsDuplicateBothTimes()
    {
        var output = await RunAsync(new JsonArray(
            Reading(NC1, "consumption", Value(Eight, 1), Value(Eight.AddMinutes(15), 1)),
            Reading(NC1, "consumption", Value(Eight, 2))));

        Assert.Equal(1, output.Accepted);
        Assert.Equal([(0, 0), (1, 0)], output.Rejected.Select(r =>
            (r!["reading"]!.GetValue<int>(), r["value"]!.GetValue<int>())));
        Assert.All(output.Rejected, r => Assert.Equal(PrepareMeterReadingsNode.DuplicateSlot, r!["code"]!.GetValue<string>()));
    }

    // ==================================================================== request level

    [Fact]
    public async Task NoReadings_IsARequestError()
    {
        foreach (var body in new JsonNode?[] { null, new JsonArray(), new JsonArray(Reading(NC1, "consumption")) })
        {
            var output = await RunAsync(body);
            Assert.Equal("error", output.Status);
            Assert.Equal("VALIDATION", output.Result["code"]!.GetValue<string>());
            Assert.Equal(0, output.Accepted);
            Assert.Empty(output.Records);
        }

        AssertNoSessionOpened();
    }

    [Fact]
    public async Task TooManyValues_IsARequestError()
    {
        var output = await RunAsync(new JsonArray(Reading(NC1, "consumption",
            Value(Eight, 1), Value(Eight.AddMinutes(15), 1), Value(Eight.AddMinutes(30), 1))), Config(maxValues: 2));

        Assert.Equal("error", output.Status);
        Assert.Contains("at most 2", output.Result["message"]!.GetValue<string>());
        Assert.Empty(output.Records);
        AssertNoSessionOpened();
    }

    [Fact]
    public async Task OnlyTheReportedNumbersAreLoaded()
    {
        await RunAsync(new JsonArray(Reading(NC1, "consumption", Value(Eight, 1))));

        Assert.NotEmpty(PointQueries);
        Assert.All(PointQueries, q => Assert.Contains(q.FieldFilters ?? [],
            f => f.AttributePath == "MeteringPointNumber"));
    }
}

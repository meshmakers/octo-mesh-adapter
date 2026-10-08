using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// CommunityEnergyBalance@1 (AB#5639, BAL-01..03): the provisional balance equals the final
/// allocation for the same inputs, the slot window, the reporting counters, the Eda abort and the
/// per metering point detail.
/// </summary>
public class CommunityEnergyBalanceNodeTests : CommunityNodeTestBase
{
    private const string TargetPath = "$.balance";
    private const string C1 = "0000000000000000000000c1";
    private const string C2 = "0000000000000000000000c2";
    private const string C3 = "0000000000000000000000c3";
    private const string P1 = "0000000000000000000000a1";
    private const string P2 = "0000000000000000000000a2";
    private const string NC1 = "AT0099980000000000000000000000C01";
    private const string NC2 = "AT0099980000000000000000000000C02";
    private const string NC3 = "AT0099980000000000000000000000C03";
    private const string NP1 = "AT0099980000000000000000000000P01";
    private const string NP2 = "AT0099980000000000000000000000P02";

    private static CommunityEnergyBalanceNodeConfiguration Config(string? slotsPath = null,
        string? meteringPointsPath = null) => new()
    {
        ArchiveRtId = ArchiveRtId.ToString(),
        TargetPath = TargetPath,
        SlotsPath = slotsPath,
        MeteringPointsPath = meteringPointsPath
    };

    private static FakeTimeProvider ClockAt(DateTime utc) => new(new DateTimeOffset(utc, TimeSpan.Zero));

    private async Task<JsonObject> RunAsync(CommunityEnergyBalanceNodeConfiguration config, DateTime now,
        Action<IDataContext>? setup = null, int? pageSize = null)
    {
        var (dataContext, nodeContext, next, _) = PrepareTestWithLogger(config);
        setup?.Invoke(dataContext);
        JsonObject? output = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set) && (string)call.Arguments[0]! == TargetPath)
            .Invokes(call => output = (JsonObject)call.Arguments[1]!);

        var node = new CommunityEnergyBalanceNode(next, EtlContext, SystemContext)
        {
            Clock = ClockAt(now),
            PageSize = pageSize ?? AllocateCommunityEnergyNode.ArchivePageSize
        };
        await node.ProcessObjectAsync(dataContext, nodeContext);
        VerifyNextCalled(next, dataContext, nodeContext);
        return output!;
    }

    private async Task<List<AllocationEnergyData>> RunAllocationAsync()
    {
        var config = new AllocateCommunityEnergyNodeConfiguration
        {
            ArchiveRtId = ArchiveRtId.ToString(),
            StartDay = Day.ToDateTime(TimeOnly.MinValue),
            NumDays = 1,
            TargetPath = "$.records"
        };
        var (dataContext, nodeContext, next, _) = PrepareTestWithLogger(config);
        List<AllocationEnergyData>? records = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set) && (string)call.Arguments[0]! == "$.records")
            .Invokes(call => records = (List<AllocationEnergyData>)call.Arguments[1]!);
        await new AllocateCommunityEnergyNode(next, EtlContext, SystemContext).ProcessObjectAsync(dataContext, nodeContext);
        return records!;
    }

    private void BuildRichWorld()
    {
        AddConsumer(C1, NC1, pf: 80);
        AddConsumer(C2, NC2, pf: 100, dataSource: 2);
        // Joins at 06:00 local time of the day under test.
        AddConsumer(C3, NC3, pf: 50, from: DayStartUtc.AddHours(6));
        AddProducer(P1, NP1, pf: 100);
        AddProducer(P2, NP2, pf: 70, dataSource: 2);

        var random = new Random(4711);
        decimal Next(int scale) => Math.Round((decimal)random.NextDouble() * scale, 3);
        var c1 = Enumerable.Range(0, 96).Select(_ => Next(1)).ToArray();
        var c2 = Enumerable.Range(0, 96).Select(_ => Next(2)).ToArray();
        var c3 = Enumerable.Range(0, 96).Select(_ => Next(1)).ToArray();
        // Generation around noon only, so some slots are short, some in surplus, some empty.
        var p1 = Enumerable.Range(0, 96).Select(i => i is > 30 and < 70 ? Next(3) : 0m).ToArray();
        var p2 = Enumerable.Range(0, 96).Select(i => i is > 36 and < 60 ? Next(2) : 0m).ToArray();

        Fill(C1, ConsumerIn, i => c1[i], quality: i => i % 7 == 0 ? 2 : 1);
        Fill(C2, ConsumerIn, i => i % 11 == 0 ? null : c2[i]); // gaps count as 0 in both runs
        Fill(C3, ConsumerIn, i => c3[i]);
        Fill(P1, ProducerIn, i => p1[i]);
        Fill(P2, ProducerIn, i => p2[i]);
    }

    // ==================================================================== equality with the final run

    [Fact]
    public async Task EverySlot_EqualsTheFinalAllocationOfTheSameInputs()
    {
        BuildRichWorld();
        var records = await RunAllocationAsync();

        var filter = new JsonArray(NC1, NC2, NC3, NP1, NP2);
        var balance = await RunAsync(Config("$.slots", "$.mps"), DayStartUtc.AddDays(1).AddMinutes(7), dc =>
        {
            A.CallTo(() => dc.GetValue("$.slots", A<bool>._)).Returns(96);
            A.CallTo(() => dc.Get<JsonNode>("$.mps")).Returns(filter);
        });

        Assert.Equal("ok", balance["status"]!.GetValue<string>());
        var slots = balance["slots"]!.AsArray();
        Assert.Equal(96, slots.Count);

        decimal Sum(string obis, DateTime from) => records.Where(r => r.MeterCode == obis)
            .SelectMany(r => r.EnergyQuantities).Where(q => q.From == from).Sum(q => q.Quantity);
        decimal Of(string rtId, string obis, DateTime from) => records
            .Single(r => r.MeteringPointRtId == rtId && r.MeterCode == obis)
            .EnergyQuantities.Single(q => q.From == from).Quantity;

        var nonTrivial = 0;
        for (var i = 0; i < 96; i++)
        {
            var slot = slots[i]!.AsObject();
            var from = DayStartUtc.AddMinutes(15 * i);
            Assert.Equal(CommunityEnergyBalanceNode.Iso(from), slot["from"]!.GetValue<string>());

            var allocated = slot["allocatedKwh"]!.GetValue<decimal>();
            Assert.Equal(Sum(ShareObis, from), allocated);
            Assert.Equal(Sum(OfferedObis, from), slot["offeredKwh"]!.GetValue<decimal>());
            Assert.Equal(Sum(SurplusObis, from), slot["surplusKwh"]!.GetValue<decimal>());
            Assert.Equal(slot["offeredKwh"]!.GetValue<decimal>() - slot["surplusKwh"]!.GetValue<decimal>(), allocated);
            if (allocated > 0 && slot["surplusKwh"]!.GetValue<decimal>() > 0) nonTrivial++;

            foreach (var mp in slot["meteringPoints"]!.AsArray())
            {
                var number = mp!["meteringPointNumber"]!.GetValue<string>();
                var registers = mp["registers"]!.AsObject();
                switch (number)
                {
                    case NC1: Assert.Equal(Of(C1, ShareObis, from), registers[ShareObis]!.GetValue<decimal>()); break;
                    case NC2: Assert.Equal(Of(C2, ShareObis, from), registers[ShareObis]!.GetValue<decimal>()); break;
                    case NC3: Assert.Equal(Of(C3, ShareObis, from), registers[ShareObis]!.GetValue<decimal>()); break;
                    case NP1:
                        Assert.Equal(Of(P1, OfferedObis, from), registers[OfferedObis]!.GetValue<decimal>());
                        Assert.Equal(Of(P1, SurplusObis, from), registers[SurplusObis]!.GetValue<decimal>());
                        break;
                    case NP2:
                        Assert.Equal(Of(P2, OfferedObis, from), registers[OfferedObis]!.GetValue<decimal>());
                        Assert.Equal(Of(P2, SurplusObis, from), registers[SurplusObis]!.GetValue<decimal>());
                        break;
                }
            }

            // C3 only participates from 06:00 local (slot 24 on).
            Assert.Equal(i >= 24, slot["meteringPoints"]!.AsArray().Any(m => m!["meteringPointNumber"]!.GetValue<string>() == NC3));
        }

        Assert.True(nonTrivial > 0, "the fixture should contain slots with both a share and a surplus");
    }

    // ==================================================================== window

    [Fact]
    public async Task Default_ReturnsTheLastFourClosedSlotsOldestFirst()
    {
        AddConsumer(C1, NC1);
        AddProducer(P1, NP1);
        Fill(C1, ConsumerIn, _ => 0.1m);
        Fill(P1, ProducerIn, _ => 0.04m);

        var now = DayStartUtc.AddHours(10).AddMinutes(7).AddSeconds(12);
        var balance = await RunAsync(Config(), now);

        var slots = balance["slots"]!.AsArray();
        Assert.Equal(4, slots.Count);
        Assert.Equal(CommunityEnergyBalanceNode.Iso(DayStartUtc.AddHours(9)), slots[0]!["from"]!.GetValue<string>());
        Assert.Equal(CommunityEnergyBalanceNode.Iso(DayStartUtc.AddHours(10)), slots[3]!["to"]!.GetValue<string>());
        Assert.Equal(CommunityEnergyBalanceNode.Iso(now), balance["generatedAt"]!.GetValue<string>());
        Assert.True(balance["provisional"]!.GetValue<bool>());
        Assert.Null(slots[0]!["meteringPoints"]);

        var slot = slots[0]!;
        Assert.Equal(0.04m, slot["productionKwh"]!.GetValue<decimal>());
        Assert.Equal(0.04m, slot["allocatedKwh"]!.GetValue<decimal>());
        Assert.Equal(0.1m, slot["consumptionKwh"]!.GetValue<decimal>());
        Assert.Equal(0.06m, slot["shortfallKwh"]!.GetValue<decimal>());
        Assert.Equal(0.4m, slot["coverage"]!.GetValue<decimal>());
        Assert.Equal(2, slot["reported"]!["expected"]!.GetValue<int>());
        Assert.Equal(2, slot["reported"]!["received"]!.GetValue<int>());
        Assert.Equal(2, slot["reported"]!["simulated"]!.GetValue<int>());
        Assert.True(balance["simulatedIncluded"]!.GetValue<bool>());
        AssertScopedSessionOpened();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(97)]
    [InlineData("x")]
    [InlineData(2.5)]
    public async Task InvalidSlotCount_IsAnErrorResultWithoutReading(object slots)
    {
        AddConsumer(C1, NC1);
        var balance = await RunAsync(Config("$.slots"), DayStartUtc.AddHours(10),
            dc => A.CallTo(() => dc.GetValue("$.slots", A<bool>._)).Returns(slots));

        Assert.Equal("error", balance["status"]!.GetValue<string>());
        Assert.Empty(balance["slots"]!.AsArray());
        AssertNoSessionOpened();
    }

    [Fact]
    public async Task SlotsFromThePath_WinOverTheConfiguration()
    {
        AddConsumer(C1, NC1);
        var balance = await RunAsync(Config("$.slots"), DayStartUtc.AddHours(10),
            dc => A.CallTo(() => dc.GetValue("$.slots", A<bool>._)).Returns(96));
        Assert.Equal(96, balance["slots"]!.AsArray().Count);
    }

    // ==================================================================== reporting state

    [Fact]
    public async Task MissingValues_CountAsZeroAndShowInTheReportingCounters()
    {
        AddConsumer(C1, NC1, dataSource: 2);
        AddConsumer(C2, NC2, dataSource: 1);
        AddProducer(P1, NP1, dataSource: 1);
        Fill(C1, ConsumerIn, _ => 0.2m);
        Fill(P1, ProducerIn, _ => 0.1m);
        // C2 (simulated) reports nothing.

        var balance = await RunAsync(Config(), DayStartUtc.AddHours(10));

        var slot = balance["slots"]!.AsArray()[0]!;
        Assert.Equal(3, slot["reported"]!["expected"]!.GetValue<int>());
        Assert.Equal(2, slot["reported"]!["received"]!.GetValue<int>());
        Assert.Equal(1, slot["reported"]!["simulated"]!.GetValue<int>());
        Assert.Equal(0.2m, slot["consumptionKwh"]!.GetValue<decimal>());
        Assert.Equal(0.1m, slot["allocatedKwh"]!.GetValue<decimal>());
        Assert.False(balance["simulatedIncluded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NoConsumption_GivesCoverageZero()
    {
        AddProducer(P1, NP1);
        Fill(P1, ProducerIn, _ => 0.1m);

        var slot = (await RunAsync(Config(), DayStartUtc.AddHours(10)))["slots"]!.AsArray()[0]!;
        Assert.Equal(0m, slot["coverage"]!.GetValue<decimal>());
        Assert.Equal(0.1m, slot["surplusKwh"]!.GetValue<decimal>());
        Assert.Equal(0m, slot["shortfallKwh"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ParticipatingEdaMeteringPoint_IsAnErrorResult()
    {
        AddConsumer(C1, NC1);
        AddConsumer(C2, NC2, dataSource: null); // missing attribute = Eda
        var balance = await RunAsync(Config(), DayStartUtc.AddHours(10));

        Assert.Equal("error", balance["status"]!.GetValue<string>());
        Assert.Contains(NC2, balance["message"]!.GetValue<string>());
        Assert.Empty(Queries);
    }

    [Fact]
    public async Task EdaMeteringPointOutsideTheWindow_DoesNotBlock()
    {
        AddConsumer(C1, NC1);
        AddConsumer(C2, NC2, dataSource: 0, from: DayStartUtc.AddDays(5));
        var balance = await RunAsync(Config(), DayStartUtc.AddHours(10));
        Assert.Equal("ok", balance["status"]!.GetValue<string>());
    }

    // ==================================================================== detail filter

    [Fact]
    public async Task Filter_ReturnsOnlyTheRequestedMeteringPoints()
    {
        AddConsumer(C1, NC1);
        AddConsumer(C2, NC2);
        AddProducer(P1, NP1);
        Fill(C1, ConsumerIn, _ => 0.1m);
        Fill(C2, ConsumerIn, _ => 0.1m);
        Fill(P1, ProducerIn, _ => 0.15m);

        var filter = new JsonArray(NC1, NP1, "AT0099980000000000000000000000X99");
        var balance = await RunAsync(Config(meteringPointsPath: "$.mps"), DayStartUtc.AddHours(10),
            dc => A.CallTo(() => dc.Get<JsonNode>("$.mps")).Returns(filter));

        var mps = balance["slots"]!.AsArray()[0]!["meteringPoints"]!.AsArray();
        Assert.Equal([NC1, NP1], mps.Select(m => m!["meteringPointNumber"]!.GetValue<string>()));
        var consumer = mps[0]!;
        Assert.Equal("consumption", consumer["direction"]!.GetValue<string>());
        Assert.Equal(0.1m, consumer["registers"]![ConsumerIn]!.GetValue<decimal>());
        Assert.Equal(0.075m, consumer["registers"]![ShareObis]!.GetValue<decimal>());
        var producer = mps[1]!;
        Assert.Equal("production", producer["direction"]!.GetValue<string>());
        Assert.Equal(0.15m, producer["registers"]![ProducerIn]!.GetValue<decimal>());
        Assert.Equal(0.15m, producer["registers"]![OfferedObis]!.GetValue<decimal>());
        Assert.Equal(0m, producer["registers"]![SurplusObis]!.GetValue<decimal>());
    }

    [Fact]
    public async Task WritesNothing()
    {
        AddConsumer(C1, NC1);
        Fill(C1, ConsumerIn, _ => 0.1m);
        await RunAsync(Config(), DayStartUtc.AddHours(10));

        A.CallTo(TenantRepository).Where(call => call.Method.Name.StartsWith("ApplyChanges", StringComparison.Ordinal))
            .MustNotHaveHappened();
    }

    [Fact]
    public void LastClosedSlots_AlignsToTheGrid()
    {
        var slots = CommunityEnergyBalanceNode.LastClosedSlots(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(15), 2);
        Assert.Equal(new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc), slots[0].From);
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc), slots[1].To);
    }
}

using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the allocation algorithm of AllocateCommunityEnergy@1 (AB#5634, ALC-01 / section 7): dynamic
/// key, cap at pf * c, everything in whole units of q, largest-remainder surplus, and the billing
/// identity sum(o - u) == sum(a) exactly.
/// </summary>
public class CommunityAllocatorTests
{
    private readonly CommunityAllocator _allocator = new(0.001m, "L3");

    private static AllocationMember M(string rtId, decimal? value, int pf = 100, string quality = "L1")
        => new(rtId, pf, value, quality);

    private static SlotAllocation Run(CommunityAllocator allocator, AllocationMember[] consumers, AllocationMember[] producers)
    {
        var result = allocator.Allocate(consumers, producers);
        AssertInvariants(allocator, consumers, producers, result);
        return result;
    }

    private SlotAllocation Run(AllocationMember[] consumers, AllocationMember[] producers)
        => Run(_allocator, consumers, producers);

    /// <summary>The ALC-01 acceptance criteria, independently of the allocator's own assertions.</summary>
    private static void AssertInvariants(CommunityAllocator allocator, AllocationMember[] consumers,
        AllocationMember[] producers, SlotAllocation r)
    {
        long sumA = 0;
        decimal w = 0;
        for (var i = 0; i < consumers.Length; i++)
        {
            var c = Math.Max(0m, consumers[i].Value ?? 0m);
            var wi = consumers[i].PartitionFactorPercent * c / 100m;
            w += wi;
            var a = r.Consumers[i].ShareUnits;
            Assert.True(a >= 0, "a_i >= 0");
            Assert.True(allocator.ToKWh(a) <= wi, $"a_i <= pf_i * c_i ({a} vs {wi})");
            Assert.True(a <= allocator.FloorUnits(wi), "a_i <= floor_q(pf_i * c_i)");
            sumA += a;
        }

        long e = 0, sumCredit = 0;
        for (var p = 0; p < producers.Length; p++)
        {
            var g = Math.Max(0m, producers[p].Value ?? 0m);
            var o = r.Producers[p].OfferedUnits;
            Assert.Equal(allocator.FloorUnits(producers[p].PartitionFactorPercent * g / 100m), o);
            var u = r.Producers[p].SurplusUnits;
            Assert.True(u >= 0 && u <= o, $"0 <= u_p <= o_p ({u}, {o})");
            e += o;
            sumCredit += o - u;
        }

        Assert.Equal(sumA, sumCredit);
        Assert.True(sumA <= e, "sum a <= E");
        Assert.Equal(e, r.OfferedUnits);
        Assert.Equal(sumA, r.AllocatedUnits);
        Assert.Equal(e - sumA, r.SurplusUnits);

        if (e > 0 && w > 0 && w <= allocator.ToKWh(e))
        {
            for (var i = 0; i < consumers.Length; i++)
            {
                var c = Math.Max(0m, consumers[i].Value ?? 0m);
                Assert.Equal(allocator.FloorUnits(consumers[i].PartitionFactorPercent * c / 100m),
                    r.Consumers[i].ShareUnits);
            }
        }
    }

    [Fact]
    public void DemandBelowOffer_EveryConsumerFullyCovered_RestIsSurplus()
    {
        var r = Run([M("c1", 1.0m), M("c2", 0.5m)], [M("p1", 3.0m)]);

        Assert.Equal(1000, r.Consumers[0].ShareUnits);
        Assert.Equal(500, r.Consumers[1].ShareUnits);
        Assert.Equal(3000, r.Producers[0].OfferedUnits);
        Assert.Equal(1500, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void DemandEqualToOffer_FullyCovered_NoSurplus()
    {
        var r = Run([M("c1", 1.0m), M("c2", 2.0m)], [M("p1", 3.0m)]);

        Assert.Equal(1000, r.Consumers[0].ShareUnits);
        Assert.Equal(2000, r.Consumers[1].ShareUnits);
        Assert.Equal(0, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void DemandAboveOffer_SharedProportionally_NothingLeft()
    {
        // E = 1.000, W = 4.000 -> a = 0.250 and 0.750.
        var r = Run([M("c1", 1.0m), M("c2", 3.0m)], [M("p1", 1.0m)]);

        Assert.Equal(250, r.Consumers[0].ShareUnits);
        Assert.Equal(750, r.Consumers[1].ShareUnits);
        Assert.Equal(0, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void DemandAboveOffer_RoundingLeftoverStaysSurplus()
    {
        // E = 1.000 over three equal consumers: 0.333 each, 0.001 stays with the producer.
        var r = Run([M("c1", 1m), M("c2", 1m), M("c3", 1m)], [M("p1", 1m)]);

        Assert.All(r.Consumers, c => Assert.Equal(333, c.ShareUnits));
        Assert.Equal(1, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void NoGeneration_AllZero_IncludingSurplus()
    {
        var r = Run([M("c1", 1.2m), M("c2", 0.4m)], [M("p1", 0m), M("p2", null)]);

        Assert.All(r.Consumers, c => Assert.Equal(0, c.ShareUnits));
        Assert.All(r.Producers, p =>
        {
            Assert.Equal(0, p.OfferedUnits);
            Assert.Equal(0, p.SurplusUnits);
        });
    }

    [Fact]
    public void NoConsumption_EverythingIsSurplus()
    {
        var r = Run([M("c1", 0m), M("c2", null)], [M("p1", 2.5m), M("p2", 0.5m)]);

        Assert.All(r.Consumers, c => Assert.Equal(0, c.ShareUnits));
        Assert.Equal(2500, r.Producers[0].SurplusUnits);
        Assert.Equal(500, r.Producers[1].SurplusUnits);
    }

    [Fact]
    public void NoProducersAtAll_ConsumersGetZero()
    {
        var r = Run([M("c1", 1m)], []);

        Assert.Equal(0, r.Consumers[0].ShareUnits);
        Assert.Equal(0, r.OfferedUnits);
    }

    [Fact]
    public void PartitionFactorBelow100_CapsConsumerAndLimitsOffer()
    {
        // Consumer pf 50 %: eligible 0.5 of 1.0. Producer pf 40 %: offers 0.4 of 1.0.
        var r = Run([M("c1", 1.0m, pf: 50)], [M("p1", 1.0m, pf: 40)]);
        Assert.Equal(400, r.Producers[0].OfferedUnits);
        Assert.Equal(400, r.Consumers[0].ShareUnits);
        Assert.Equal(0, r.Producers[0].SurplusUnits);

        var covered = Run([M("c1", 1.0m, pf: 50)], [M("p1", 2.0m, pf: 40)]);
        Assert.Equal(800, covered.Producers[0].OfferedUnits);
        Assert.Equal(500, covered.Consumers[0].ShareUnits); // capped at pf * c
        Assert.Equal(300, covered.Producers[0].SurplusUnits);
    }

    [Fact]
    public void PartitionFactorWeightsTheProportionalKey()
    {
        // w = 0.5 * 2 = 1.0 and 1.0 * 1 = 1.0 -> equal shares of E = 1.0.
        var r = Run([M("c1", 2.0m, pf: 50), M("c2", 1.0m)], [M("p1", 1.0m)]);

        Assert.Equal(500, r.Consumers[0].ShareUnits);
        Assert.Equal(500, r.Consumers[1].ShareUnits);
    }

    [Fact]
    public void MissingConsumer_GetsZeroWithMissingQuality_OthersTakeTheShare()
    {
        var r = Run([M("c1", null), M("c2", 1.0m, quality: "L2")], [M("p1", 0.6m)]);

        Assert.Equal(0, r.Consumers[0].ShareUnits);
        Assert.Equal("L3", r.Consumers[0].Quality);
        Assert.True(r.Consumers[0].Missing);
        Assert.Equal(600, r.Consumers[1].ShareUnits);
        // V-2: the slot has a missing input, so every register carries MissingQuality.
        Assert.Equal("L3", r.Consumers[1].Quality);
        Assert.Equal("L3", r.Producers[0].Quality);
    }

    [Fact]
    public void MissingProducer_OffersZeroWithMissingQuality()
    {
        var r = Run([M("c1", 1.0m)], [M("p1", null), M("p2", 0.4m)]);

        Assert.Equal(0, r.Producers[0].OfferedUnits);
        Assert.Equal(0, r.Producers[0].SurplusUnits);
        Assert.Equal("L3", r.Producers[0].Quality);
        Assert.Equal(400, r.Producers[1].OfferedUnits);
        Assert.Equal(400, r.Consumers[0].ShareUnits);
    }

    [Theory]
    [InlineData("L1", "L1", "L1", "L1")]
    [InlineData("L1", "L2", "L1", "L2")]
    [InlineData("L2", "L1", "Manual", "Manual")]
    [InlineData("Manual", "L3", "L1", "L3")]
    [InlineData("L1", "L1", "X", "X")]
    public void EveryRegister_CarriesTheWorstInputQualityOfTheSlot(string c1, string c2, string p1, string expected)
    {
        // V-2: L1 < L2 < Manual < L3 < anything unknown.
        var r = Run([M("c1", 0.4m, quality: c1), M("c2", 0.2m, quality: c2)], [M("p1", 1.0m, quality: p1)]);

        Assert.All(r.Consumers, c => Assert.Equal(expected, c.Quality));
        Assert.Equal(expected, r.Producers[0].Quality);
    }

    [Fact]
    public void QualityRank_OrdersMeasuredBeforeManualBeforeEstimate()
    {
        Assert.True(CommunityAllocator.QualityRank("L1") < CommunityAllocator.QualityRank("L2"));
        Assert.True(CommunityAllocator.QualityRank("L2") < CommunityAllocator.QualityRank("Manual"));
        Assert.True(CommunityAllocator.QualityRank("Manual") < CommunityAllocator.QualityRank("L3"));
        Assert.True(CommunityAllocator.QualityRank("L3") < CommunityAllocator.QualityRank("Unknown"));
    }

    [Fact]
    public void ValuesNotMultiplesOfQ_AreFlooredNeverRoundedUp()
    {
        // g = 0.0019999 -> o = 0.001; c = 0.0009999 -> w < q -> a = 0.
        var r = Run([M("c1", 0.0009999m)], [M("p1", 0.0019999m)]);

        Assert.Equal(1, r.Producers[0].OfferedUnits);
        Assert.Equal(0, r.Consumers[0].ShareUnits);
        Assert.Equal(1, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void ManyTinyConsumers_ShareTheOfferDownToZero()
    {
        var consumers = Enumerable.Range(0, 1000).Select(i => M($"c{i:D4}", 0.0004m)).ToArray();
        var r = Run(consumers, [M("p1", 0.1m)]);

        // W = 0.4 > E = 0.1; each would get 0.00004 -> floor 0; everything stays surplus.
        Assert.All(r.Consumers, c => Assert.Equal(0, c.ShareUnits));
        Assert.Equal(100, r.Producers[0].SurplusUnits);
    }

    [Fact]
    public void SurplusRemainder_GoesToLargestFractionalRemainder()
    {
        // E = 0.007 (o = 3, 4), A = 0 (no consumers) -> S = 7 -> exact split 3 / 4.
        var exact = Run([], [M("p1", 0.003m), M("p2", 0.004m)]);
        Assert.Equal(3, exact.Producers[0].SurplusUnits);
        Assert.Equal(4, exact.Producers[1].SurplusUnits);

        // o = 1, 2 (E = 3), consumer takes 1 -> S = 2: 2*1/3 = 0.67 , 2*2/3 = 1.33 -> floors 0 and 1,
        // remainder 1 to the larger fraction (p1: .67).
        var r = Run([M("c1", 0.001m)], [M("p1", 0.001m), M("p2", 0.002m)]);
        Assert.Equal(1, r.Consumers[0].ShareUnits);
        Assert.Equal(1, r.Producers[0].SurplusUnits);
        Assert.Equal(1, r.Producers[1].SurplusUnits);
    }

    [Fact]
    public void SurplusTie_BrokenByRtIdOrdinal()
    {
        // Three equal producers o = 1 each, E = 3, consumer takes 1 -> S = 2, remainders all equal
        // (2/3); the two lowest rtIds (ordinal) get one unit each, whatever the input order.
        var r = Run([M("c1", 0.001m)], [M("pB", 0.001m), M("pC", 0.001m), M("pA", 0.001m)]);

        Assert.Equal(1, r.Producers.Single(p => p.RtId == "pA").SurplusUnits);
        Assert.Equal(1, r.Producers.Single(p => p.RtId == "pB").SurplusUnits);
        Assert.Equal(0, r.Producers.Single(p => p.RtId == "pC").SurplusUnits);

        // Ordinal, not culture: "Z" sorts before "a".
        var ordinal = Run([M("c1", 0.002m)], [M("a", 0.001m), M("Z", 0.001m), M("b", 0.001m)]);
        Assert.Equal(1, ordinal.Producers.Single(p => p.RtId == "Z").SurplusUnits);
    }

    [Fact]
    public void NegativeValues_TreatedAsZeroAndCounted()
    {
        var r = Run([M("c1", -1m), M("c2", 1m)], [M("p1", -2m), M("p2", 1m)]);

        Assert.Equal(2, r.NegativeValues);
        Assert.Equal(0, r.Consumers[0].ShareUnits);
        Assert.Equal(0, r.Producers[0].OfferedUnits);
        Assert.Equal(1000, r.Consumers[1].ShareUnits);
    }

    [Fact]
    public void OtherResolution_IsHonoured()
    {
        var allocator = new CommunityAllocator(0.01m, "L3");
        var r = Run(allocator, [M("c1", 1m), M("c2", 1m), M("c3", 1m)], [M("p1", 1m)]);

        Assert.All(r.Consumers, c => Assert.Equal(33, c.ShareUnits));
        Assert.Equal(1, r.Producers[0].SurplusUnits);
        Assert.Equal(0.33m, allocator.ToKWh(r.Consumers[0].ShareUnits));
    }

    [Fact]
    public void HugeAndHighPrecisionValues_StayExact()
    {
        // Mixed scales (28-digit fractions next to large integers) must not overflow or round across
        // a unit boundary.
        var r = Run(
            [M("c1", 123456789.123456789m, pf: 37), M("c2", 0.0000000000000000000000000001m), M("c3", 98765.4321m, pf: 99)],
            [M("p1", 1000000m, pf: 13), M("p2", 7.77777m, pf: 100)]);

        Assert.True(r.AllocatedUnits > 0);
    }

    [Fact]
    public void RandomisedProperty_AllInvariantsHoldExactly()
    {
        // Seeded so a failure is reproducible.
        var random = new Random(5634);
        for (var slot = 0; slot < 5000; slot++)
        {
            var consumers = Enumerable.Range(0, random.Next(0, 12))
                .Select(i => M($"c{random.Next(0, 1000):D3}{i}", RandomValue(random), RandomPf(random),
                    random.Next(3) switch { 0 => "L1", 1 => "L2", _ => "L3" }))
                .ToArray();
            var producers = Enumerable.Range(0, random.Next(0, 6))
                .Select(i => M($"p{random.Next(0, 1000):D3}{i}", RandomValue(random), RandomPf(random)))
                .ToArray();

            var allocator = random.Next(4) == 0 ? new CommunityAllocator(0.01m, "L3") : _allocator;
            var r = Run(allocator, consumers, producers);

            var worst = consumers.Concat(producers)
                .Select(m => m.Value is null ? "L3" : m.Quality)
                .OrderByDescending(CommunityAllocator.QualityRank)
                .FirstOrDefault() ?? "L3";
            Assert.All(r.Consumers, c => Assert.Equal(worst, c.Quality));
            Assert.All(r.Producers, p => Assert.Equal(worst, p.Quality));
        }
    }

    private static decimal? RandomValue(Random random)
        => random.Next(10) switch
        {
            0 => null,
            1 => 0m,
            2 => Math.Round((decimal)random.NextDouble() * 0.002m, 7), // around q
            3 => Math.Round((decimal)random.NextDouble() * 1000m, 4),
            _ => Math.Round((decimal)random.NextDouble() * 5m, random.Next(0, 10))
        };

    private static int RandomPf(Random random)
        => random.Next(4) == 0 ? 100 : random.Next(0, 101);
}

using System.Numerics;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

/// <summary>One participating member (consumer or producer) of one slot.</summary>
/// <param name="RtId">Metering point rtId; the ordinal tie breaker of the remainder distribution.</param>
/// <param name="PartitionFactorPercent">Partition factor in percent, 0 to 100.</param>
/// <param name="Value">Raw slot quantity in kWh, or null when the value is missing.</param>
/// <param name="Quality">Quality of the raw value (ignored when the value is missing).</param>
internal readonly record struct AllocationMember(
    string RtId,
    int PartitionFactorPercent,
    decimal? Value,
    string Quality);

/// <summary>Consumer result of one slot.</summary>
/// <param name="RtId">Metering point rtId.</param>
/// <param name="ShareUnits">Community share a_i in whole units of the resolution.</param>
/// <param name="Quality">Quality written with the share.</param>
/// <param name="Missing">True when the raw value was missing and counted as 0.</param>
internal readonly record struct ConsumerAllocation(string RtId, long ShareUnits, string Quality, bool Missing);

/// <summary>Producer result of one slot.</summary>
/// <param name="RtId">Metering point rtId.</param>
/// <param name="OfferedUnits">Offered generation o_p in whole units of the resolution.</param>
/// <param name="SurplusUnits">Undistributed surplus u_p in whole units of the resolution.</param>
/// <param name="Quality">Quality written with both registers.</param>
/// <param name="Missing">True when the raw value was missing and counted as 0.</param>
internal readonly record struct ProducerAllocation(
    string RtId, long OfferedUnits, long SurplusUnits, string Quality, bool Missing);

/// <summary>Result of one slot, consumers and producers in input order.</summary>
internal sealed record SlotAllocation(
    IReadOnlyList<ConsumerAllocation> Consumers,
    IReadOnlyList<ProducerAllocation> Producers,
    decimal ConsumptionKWh,
    decimal EligibleKWh,
    long OfferedUnits,
    long AllocatedUnits,
    long SurplusUnits,
    int NegativeValues);

/// <summary>
/// The pure allocation algorithm of <c>AllocateCommunityEnergy@1</c> for one slot (requirements
/// section 7, ALC-01): dynamic key, capped at the partition-weighted consumption, everything in whole
/// multiples of the resolution q, and the billing identity sum(o_p - u_p) == sum(a_i) exact.
/// </summary>
/// <remarks>
/// <para>
/// The proportional share is computed with integers only. With w_i = pf_i * c_i written as
/// N_i / D over one common denominator, floor_q(E * w_i / W) is floor(E_units * N_i / sum N) —
/// q and D cancel — so no decimal division can round a quotient across an integer boundary. The
/// products run in <see cref="BigInteger"/> (unit counts times decimal mantissas exceed 64 bits).
/// </para>
/// <para>
/// Every invariant is asserted after the computation; a violation throws, it is never repaired.
/// </para>
/// </remarks>
internal sealed class CommunityAllocator
{
    private readonly BigInteger _qNumerator;
    private readonly BigInteger _qDenominator;

    /// <param name="resolution">The quantity step q in kWh, greater than 0.</param>
    /// <param name="missingQuality">Quality written for a missing value.</param>
    public CommunityAllocator(decimal resolution, string missingQuality)
    {
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution,
                "The resolution must be greater than 0.");
        }

        Resolution = resolution;
        MissingQuality = missingQuality;
        (_qNumerator, _qDenominator) = ToFraction(resolution);
    }

    /// <summary>The quantity step q in kWh.</summary>
    public decimal Resolution { get; }

    /// <summary>Quality written for a missing value.</summary>
    public string MissingQuality { get; }

    /// <summary>Converts whole units back into kWh.</summary>
    public decimal ToKWh(long units) => units * Resolution;

    /// <summary>floor(x / q) for x &gt;= 0, exact.</summary>
    public long FloorUnits(decimal kWh)
    {
        if (kWh <= 0)
        {
            return 0;
        }

        var (n, d) = ToFraction(kWh);
        // x / q = (n / d) / (qn / qd) = n * qd / (d * qn); both sides positive, so truncation is floor.
        return checked((long)BigInteger.Divide(n * _qDenominator, d * _qNumerator));
    }

    /// <summary>Allocates one slot.</summary>
    /// <param name="consumers">Consumers participating in the slot.</param>
    /// <param name="producers">Producers participating in the slot.</param>
    public SlotAllocation Allocate(IReadOnlyList<AllocationMember> consumers, IReadOnlyList<AllocationMember> producers)
    {
        var negative = 0;

        // ---- producers: o_p = floor_q(pf_p * g_p), E = sum o_p
        var offered = new long[producers.Count];
        long e = 0;
        for (var p = 0; p < producers.Count; p++)
        {
            var g = Sanitise(producers[p].Value, ref negative);
            offered[p] = FloorUnits(Weighted(producers[p].PartitionFactorPercent, g));
            e = checked(e + offered[p]);
        }

        // ---- consumers: w_i = pf_i * c_i (exact), W = sum w_i
        var weights = new decimal[consumers.Count];
        decimal w = 0;
        decimal consumption = 0;
        for (var i = 0; i < consumers.Count; i++)
        {
            var c = Sanitise(consumers[i].Value, ref negative);
            consumption += c;
            weights[i] = Weighted(consumers[i].PartitionFactorPercent, c);
            w += weights[i];
        }

        var shares = new long[consumers.Count];
        if (e > 0 && w > 0)
        {
            // W <= E (in kWh): W <= E_units * q, i.e. (Wn / Wd) <= E * qn / qd.
            var (wn, wd) = ToFraction(w);
            var fullyCovered = wn * _qDenominator <= new BigInteger(e) * _qNumerator * wd;

            if (fullyCovered)
            {
                for (var i = 0; i < shares.Length; i++)
                {
                    shares[i] = FloorUnits(weights[i]);
                }
            }
            else
            {
                // Common denominator for every w_i: the largest decimal scale among them.
                var scale = weights.Max(Scale);
                var numerators = weights.Select(x => ScaledNumerator(x, scale)).ToArray();
                var total = numerators.Aggregate(BigInteger.Zero, (acc, n) => acc + n);
                var eBig = new BigInteger(e);
                for (var i = 0; i < shares.Length; i++)
                {
                    shares[i] = (long)BigInteger.Divide(eBig * numerators[i], total);
                }
            }
        }

        long a = 0;
        foreach (var share in shares)
        {
            a = checked(a + share);
        }

        var s = e - a;

        // ---- surplus: u_p = floor(S * o_p / E), remainder by largest fractional part, ties by rtId
        var surplus = new long[producers.Count];
        if (e > 0 && s > 0)
        {
            var remainders = new Int128[producers.Count];
            long assigned = 0;
            for (var p = 0; p < producers.Count; p++)
            {
                var product = (Int128)s * offered[p];
                surplus[p] = (long)(product / e);
                remainders[p] = product % e;
                assigned += surplus[p];
            }

            var rest = s - assigned;
            if (rest > 0)
            {
                var order = Enumerable.Range(0, producers.Count)
                    .Where(p => remainders[p] > 0 && surplus[p] < offered[p])
                    .OrderByDescending(p => remainders[p])
                    .ThenBy(p => producers[p].RtId, StringComparer.Ordinal)
                    .ToList();

                if (order.Count < rest)
                {
                    throw new InvalidOperationException(
                        $"AllocateCommunityEnergy: cannot distribute a surplus remainder of {rest} unit(s) " +
                        $"over {order.Count} producer(s).");
                }

                for (var k = 0; k < rest; k++)
                {
                    surplus[order[k]]++;
                }
            }
        }

        var consumerResults = new ConsumerAllocation[consumers.Count];
        for (var i = 0; i < consumers.Count; i++)
        {
            var missing = consumers[i].Value is null;
            consumerResults[i] = new ConsumerAllocation(consumers[i].RtId, shares[i],
                missing ? MissingQuality : consumers[i].Quality, missing);
        }

        var producerResults = new ProducerAllocation[producers.Count];
        for (var p = 0; p < producers.Count; p++)
        {
            var missing = producers[p].Value is null;
            producerResults[p] = new ProducerAllocation(producers[p].RtId, offered[p], surplus[p],
                missing ? MissingQuality : producers[p].Quality, missing);
        }

        var result = new SlotAllocation(consumerResults, producerResults, consumption, w, e, a, s, negative);
        AssertInvariants(result, weights);
        return result;
    }

    /// <summary>
    /// Asserts, in whole units: 0 &lt;= a_i, a_i * q &lt;= w_i, 0 &lt;= u_p &lt;= o_p,
    /// sum(o_p - u_p) == sum(a_i), A &lt;= E, and W &lt;= E implies a_i == floor_q(w_i).
    /// </summary>
    private void AssertInvariants(SlotAllocation r, decimal[] weights)
    {
        long sumShares = 0;
        for (var i = 0; i < r.Consumers.Count; i++)
        {
            var share = r.Consumers[i].ShareUnits;
            var cap = FloorUnits(weights[i]);
            if (share < 0 || share > cap)
            {
                Fail($"consumer {r.Consumers[i].RtId} share {share} outside [0, {cap}]");
            }

            sumShares += share;
        }

        long sumCredited = 0;
        foreach (var p in r.Producers)
        {
            if (p.OfferedUnits < 0 || p.SurplusUnits < 0 || p.SurplusUnits > p.OfferedUnits)
            {
                Fail($"producer {p.RtId} surplus {p.SurplusUnits} outside [0, {p.OfferedUnits}]");
            }

            sumCredited += p.OfferedUnits - p.SurplusUnits;
        }

        if (sumCredited != sumShares)
        {
            Fail($"sum(o - u) = {sumCredited} differs from sum(a) = {sumShares}");
        }

        if (sumShares != r.AllocatedUnits || r.AllocatedUnits > r.OfferedUnits || r.SurplusUnits < 0)
        {
            Fail($"A = {r.AllocatedUnits} exceeds E = {r.OfferedUnits}");
        }

        if (r.OfferedUnits > 0 && r.EligibleKWh > 0 && r.EligibleKWh <= ToKWh(r.OfferedUnits))
        {
            for (var i = 0; i < r.Consumers.Count; i++)
            {
                if (r.Consumers[i].ShareUnits != FloorUnits(weights[i]))
                {
                    Fail($"W <= E but consumer {r.Consumers[i].RtId} is not fully covered");
                }
            }
        }
    }

    private static void Fail(string message)
        => throw new InvalidOperationException($"AllocateCommunityEnergy invariant violated: {message}.");

    private static decimal Sanitise(decimal? value, ref int negative)
    {
        if (value is not { } v)
        {
            return 0;
        }

        if (v < 0)
        {
            negative++;
            return 0;
        }

        return v;
    }

    /// <summary>pf * x with pf = percent / 100; exact in decimal.</summary>
    private static decimal Weighted(int percent, decimal x) => percent * x / 100m;

    private static int Scale(decimal value) => value.Scale;

    /// <summary>value * 10^scale as an integer; value must have a scale not above <paramref name="scale"/>.</summary>
    private static BigInteger ScaledNumerator(decimal value, int scale)
    {
        var (n, _) = ToFraction(value);
        return n * BigInteger.Pow(10, scale - value.Scale);
    }

    /// <summary>A decimal as mantissa / 10^scale.</summary>
    private static (BigInteger Numerator, BigInteger Denominator) ToFraction(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var mantissa = new BigInteger((uint)bits[0])
                       + (new BigInteger((uint)bits[1]) << 32)
                       + (new BigInteger((uint)bits[2]) << 64);
        if (value < 0)
        {
            mantissa = -mantissa;
        }

        return (mantissa, BigInteger.Pow(10, value.Scale));
    }
}

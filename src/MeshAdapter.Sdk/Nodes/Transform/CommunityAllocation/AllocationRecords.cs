namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

// The property names below are a wire contract: they are what the EDA ingest (octo-adapter-eda,
// Dto/EnergyData.cs and EnergyQuantity.cs) emits, and SaveTimeRangeSeriesInArchive@1 is configured
// against them in handle-daten-crmsg.yaml. Do not rename.

/// <summary>One series: one metering point, one register, one day.</summary>
internal sealed class AllocationEnergyData
{
    public required string MeteringPointRtId { get; init; }
    public string? MeteringPointNumber { get; init; }
    public required string MeterCode { get; init; }
    public required string QuantityUnit { get; init; }
    public required DateTime CreationTime { get; init; }
    public required DateTime PeriodStart { get; init; }
    public required DateTime PeriodEnd { get; init; }
    public required List<AllocationEnergyQuantity> EnergyQuantities { get; init; }
}

/// <summary>One slot value of a series.</summary>
internal sealed class AllocationEnergyQuantity
{
    public required string MeteringPointRtId { get; init; }
    public string? MeteringPointNumber { get; init; }
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required decimal Quantity { get; init; }
    public required string Quality { get; init; }
}

/// <summary>Per-day summary written to the summary target path.</summary>
internal sealed class AllocationDaySummary
{
    public const string StatusAllocated = "allocated";
    public const string StatusAborted = "aborted";
    public const string StatusEmpty = "empty";

    public required string Day { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public int Slots { get; init; }
    public int Consumers { get; init; }
    public int Producers { get; init; }
    public decimal ConsumptionKWh { get; init; }
    public decimal EligibleKWh { get; init; }
    public decimal OfferedKWh { get; init; }
    public decimal AllocatedKWh { get; init; }
    public decimal SurplusKWh { get; init; }
    public int MissingConsumerValues { get; init; }
    public int MissingProducerValues { get; init; }
    public decimal? Coverage { get; init; }
    public decimal? SurplusRatio { get; init; }
    public required DateTime CutOff { get; init; }
}

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// CommunityEnergyBalance node configuration (AB#5639, requirements BAL-01..03). Computes the
/// provisional balance of an energy community for the last closed slots with exactly the allocation
/// of <c>AllocateCommunityEnergy@1</c> (same member loading, same archive read, same algorithm) and
/// writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// A slot is closed when its end is not after now. Missing raw values count as 0, as in the final
/// run, so a slot whose inputs are complete gives the numbers the final run will write. A
/// participating metering point whose data source is not allocatable (for example a real EDA
/// metering point) makes the result <c>status: error</c>, because the final run would abort that day.
/// </para>
/// <para>
/// The result written to <see cref="TargetPath"/> is an object with <c>status</c>, <c>message</c>,
/// <c>generatedAt</c>, <c>provisional</c> (always true), <c>simulatedIncluded</c> and <c>slots</c>
/// (oldest first). Each slot carries <c>from</c>, <c>to</c>, <c>productionKwh</c>,
/// <c>offeredKwh</c>, <c>allocatedKwh</c>, <c>surplusKwh</c>, <c>consumptionKwh</c>,
/// <c>shortfallKwh</c>, <c>coverage</c> (allocated / consumption, 3 decimals, 0 without
/// consumption), <c>reported</c> (<c>expected</c>, <c>received</c>, <c>simulated</c>) and, when a
/// metering point filter is given, <c>meteringPoints</c>.
/// </para>
/// </remarks>
[NodeName("CommunityEnergyBalance", 1)]
public record CommunityEnergyBalanceNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    // ------------------------------------------------------------------ Window

    /// <summary>Number of closed slots to compute, counted back from now. Default 4.</summary>
    [PropertyGroup("Window", 0)]
    public int Slots { get; init; } = 4;

    /// <summary>
    /// Optional JSONPath to the number of slots (for example <c>$.body.slots</c>). Wins over
    /// <see cref="Slots"/>; a path that resolves to nothing or null counts as not set. A value that is
    /// not a whole number between 1 and <see cref="MaxSlots"/> makes the result <c>status: error</c>.
    /// </summary>
    [PropertyGroup("Window", 1, "jsonpath")]
    public string? SlotsPath { get; init; }

    /// <summary>Upper limit for the number of slots. Default 96 (one day of 15-minute slots).</summary>
    [PropertyGroup("Window", 2)]
    public int MaxSlots { get; init; } = 96;

    /// <summary>
    /// Length of one slot. Default 15 minutes. Slots are aligned to whole multiples of this length
    /// since midnight UTC, which for 15 minutes is also the local quarter-hour grid.
    /// </summary>
    [PropertyGroup("Window", 3)]
    public TimeSpan SlotLength { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// IANA or Windows time zone id; validated, kept for symmetry with <c>AllocateCommunityEnergy@1</c>.
    /// Default <c>Europe/Vienna</c>.
    /// </summary>
    [PropertyGroup("Window", 4)]
    public string TimeZone { get; init; } = "Europe/Vienna";

    /// <summary>
    /// Optional JSONPath to a list of metering point numbers (for example
    /// <c>$.body.meteringPoints</c>). When it resolves to a non-empty list, every slot carries
    /// <c>meteringPoints</c> with the registers of exactly those metering points (BAL-03); unknown
    /// numbers are left out.
    /// </summary>
    [PropertyGroup("Window", 5, "jsonpath")]
    public string? MeteringPointsPath { get; init; }

    // ------------------------------------------------------------------ Schema

    /// <summary>CkTypeId of the consumer metering points. Default <c>EnergyCommunity/Consumer</c>.</summary>
    [PropertyGroup("Schema", 0, "ckTypeSelector")]
    public string ConsumerCkTypeId { get; init; } = "EnergyCommunity/Consumer";

    /// <summary>CkTypeId of the producer metering points. Default <c>EnergyCommunity/Producer</c>.</summary>
    [PropertyGroup("Schema", 1, "ckTypeSelector")]
    public string ProducerCkTypeId { get; init; } = "EnergyCommunity/Producer";

    /// <summary>
    /// CkTypeId of the base metering point type, used as the target type constraint when navigating
    /// from a measurement anchor or a participation period. Default <c>Basic.Energy/MeteringPoint</c>.
    /// </summary>
    [PropertyGroup("Schema", 2, "ckTypeSelector")]
    public string MeteringPointCkTypeId { get; init; } = "Basic.Energy/MeteringPoint";

    /// <summary>CkTypeId of the measurement anchors. Default <c>Basic.Energy/EnergyMeasurement</c>.</summary>
    [PropertyGroup("Schema", 3, "ckTypeSelector")]
    public string EnergyMeasurementCkTypeId { get; init; } = "Basic.Energy/EnergyMeasurement";

    /// <summary>Association role from a measurement anchor to its metering point. Default <c>System/ParentChild</c>.</summary>
    [PropertyGroup("Schema", 4)]
    public string ParentAssociationRoleId { get; init; } = "System/ParentChild";

    /// <summary>CkTypeId of the participation periods. Default <c>EnergyCommunity/ParticipationPeriod</c>.</summary>
    [PropertyGroup("Schema", 5, "ckTypeSelector")]
    public string ParticipationPeriodCkTypeId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>
    /// Association role from a participation period to its metering point. Default
    /// <c>EnergyCommunity/ParticipationPeriod</c>.
    /// </summary>
    [PropertyGroup("Schema", 6)]
    public string ParticipationPeriodAssociationRoleId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>Record attribute on the participation period with <c>From</c> and <c>To</c>. Default <c>TimeRange</c>.</summary>
    [PropertyGroup("Schema", 7)]
    public string ParticipationTimeRangeAttribute { get; init; } = "TimeRange";

    /// <summary>Integer attribute with the partition factor in percent. Default <c>PartitionFactor</c>.</summary>
    [PropertyGroup("Schema", 8)]
    public string PartitionFactorAttribute { get; init; } = "PartitionFactor";

    /// <summary>Partition factor used when the attribute is missing. Default 100.</summary>
    [PropertyGroup("Schema", 9)]
    public int DefaultPartitionFactor { get; init; } = 100;

    /// <summary>String attribute with the metering point number. Default <c>MeteringPointNumber</c>.</summary>
    [PropertyGroup("Schema", 10)]
    public string MeteringPointNumberAttribute { get; init; } = "MeteringPointNumber";

    /// <summary>
    /// Enum attribute naming where the values of a metering point come from (0 Eda, 1 Simulated,
    /// 2 SelfReported). Default <c>MeteringDataSource</c>.
    /// </summary>
    [PropertyGroup("Schema", 11)]
    public string DataSourceAttribute { get; init; } = "MeteringDataSource";

    /// <summary>Data source keys the allocation may run for. Default 1 (Simulated) and 2 (SelfReported).</summary>
    [PropertyGroup("Schema", 12)]
    public List<int> AllocatableDataSources { get; init; } = [1, 2];

    /// <summary>Data source assumed when the attribute is missing. Default 0 (Eda, not allocatable).</summary>
    [PropertyGroup("Schema", 13)]
    public int DefaultDataSource { get; init; } = 0;

    /// <summary>Data source key of simulated metering points (BAL-02). Default 1.</summary>
    [PropertyGroup("Schema", 14)]
    public int SimulatedDataSource { get; init; } = 1;

    /// <summary>String attribute on the measurement anchor carrying its OBIS code. Default <c>ObisCode</c>.</summary>
    [PropertyGroup("Schema", 15)]
    public string ObisCodeAttribute { get; init; } = "ObisCode";

    // ------------------------------------------------------------------ Archive

    /// <summary>Runtime id of the time-range archive holding the raw registers.</summary>
    [PropertyGroup("Archive", 0)]
    public required string ArchiveRtId { get; init; }

    /// <summary>Archive column with the slot quantity in kWh. Default <c>Amount.Value</c>.</summary>
    [PropertyGroup("Archive", 1)]
    public string ValueColumn { get; init; } = "Amount.Value";

    /// <summary>Archive column with the data quality key. Default <c>DataQuality</c>.</summary>
    [PropertyGroup("Archive", 2)]
    public string QualityColumn { get; init; } = "DataQuality";

    // ------------------------------------------------------------------ Registers

    /// <summary>OBIS code of the consumer input. Default <c>1-1:1.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 0)]
    public string ConsumerInputObis { get; init; } = "1-1:1.9.0 G.01";

    /// <summary>OBIS code of the producer input. Default <c>1-1:2.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 1)]
    public string ProducerInputObis { get; init; } = "1-1:2.9.0 G.01";

    /// <summary>Register name of the consumer share in the per metering point output. Default <c>1-1:2.9.0 G.03</c>.</summary>
    [PropertyGroup("Registers", 2)]
    public string ConsumerShareObis { get; init; } = "1-1:2.9.0 G.03";

    /// <summary>Register name of the offered generation in the per metering point output. Default <c>1-1:2.9.0 G.01T</c>.</summary>
    [PropertyGroup("Registers", 3)]
    public string ProducerOfferedObis { get; init; } = "1-1:2.9.0 G.01T";

    /// <summary>Register name of the undistributed surplus in the per metering point output. Default <c>1-1:2.9.0 P.01T</c>.</summary>
    [PropertyGroup("Registers", 4)]
    public string ProducerSurplusObis { get; init; } = "1-1:2.9.0 P.01T";

    // ------------------------------------------------------------------ Output

    /// <summary>Smallest quantity step in kWh, as in <c>AllocateCommunityEnergy@1</c>. Default 0.001.</summary>
    [PropertyGroup("Output", 0)]
    public decimal Resolution { get; init; } = 0.001m;

    /// <summary>Quality of a missing input value (the value counts as 0). Default <c>L3</c>.</summary>
    [PropertyGroup("Output", 1)]
    public string MissingQuality { get; init; } = "L3";

    /// <summary>JSONPath where the balance result object is written.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string TargetPath { get; init; }
}

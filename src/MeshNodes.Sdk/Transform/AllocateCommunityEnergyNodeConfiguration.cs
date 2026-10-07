using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// AllocateCommunityEnergy node configuration (AB#5634). Distributes the generation offered to an
/// energy community among its participating consumers, slot by slot, for whole calendar days, with
/// the dynamic key: every consumer is covered in proportion to its partition-weighted consumption and
/// never above it, and what is left over stays with the producers as undistributed surplus.
/// </summary>
/// <remarks>
/// <para>
/// Inputs are the raw registers of the participating metering points (consumption for consumers,
/// generation for producers), read from a time-range archive, plus the participation periods and
/// the partition factor of every metering point. The output is one record per metering point,
/// register and day in the shape the EDA ingest produces, so <c>SaveTimeRangeSeriesInArchive@1</c>
/// writes it with the configuration the EDA pipeline uses, unchanged: the community share for every
/// consumer, and the offered generation plus the undistributed surplus for every producer, both for
/// every participating slot, also when zero.
/// </para>
/// <para>
/// All arithmetic runs in whole multiples of <see cref="Resolution"/>, so the billing identity
/// "sum of offered minus surplus over the producers equals the sum of the consumer shares" holds
/// exactly in every slot. A day on which a metering point participates whose data source is not
/// allocatable (for example a real EDA metering point) is aborted as a whole and produces no records.
/// </para>
/// </remarks>
[NodeName("AllocateCommunityEnergy", 1)]
public record AllocateCommunityEnergyNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    // ------------------------------------------------------------------ Window

    /// <summary>
    /// First calendar day to allocate, in <see cref="TimeZone"/>. Only the date part is used. When
    /// neither this nor <see cref="StartDayPath"/> resolves a value, the node picks the latest day
    /// whose cut-off has already passed and allocates exactly that one day.
    /// </summary>
    [PropertyGroup("Window", 0)]
    public DateTime? StartDay { get; init; }

    /// <summary>
    /// Optional JSONPath to the first day (for example <c>$.startDay</c> with the value
    /// <c>2026-11-01</c>). Wins over <see cref="StartDay"/>; a path that resolves to nothing or to
    /// null counts as not set.
    /// </summary>
    [PropertyGroup("Window", 1, "jsonpath")]
    public string? StartDayPath { get; init; }

    /// <summary>Number of consecutive days to allocate. Default 1.</summary>
    [PropertyGroup("Window", 2)]
    public int? NumDays { get; init; } = 1;

    /// <summary>
    /// Optional JSONPath to the number of days (for example <c>$.numDays</c>). Wins over
    /// <see cref="NumDays"/>; a path that resolves to nothing counts as not set.
    /// </summary>
    [PropertyGroup("Window", 3, "jsonpath")]
    public string? NumDaysPath { get; init; }

    /// <summary>
    /// Guard against accidental long backfills: a request for more days than this fails the node.
    /// Default 31.
    /// </summary>
    [PropertyGroup("Window", 4)]
    public int MaxDays { get; init; } = 31;

    /// <summary>
    /// IANA or Windows time zone id the calendar days are cut in. Default <c>Europe/Vienna</c>, which
    /// gives 92 slots on the spring-forward day and 100 on the fall-back day.
    /// </summary>
    [PropertyGroup("Window", 5)]
    public string TimeZone { get; init; } = "Europe/Vienna";

    /// <summary>Optional JSONPath overriding <see cref="TimeZone"/>.</summary>
    [PropertyGroup("Window", 6, "jsonpath")]
    public string? TimeZonePath { get; init; }

    /// <summary>
    /// The cut-off of day D is local day D plus this many days, at <see cref="CutOffTime"/>. Default 1.
    /// It is also the deterministic creation time stamped on every record of D.
    /// </summary>
    [PropertyGroup("Window", 7)]
    public int CutOffDayOffset { get; init; } = 1;

    /// <summary>Local time of day of the cut-off. Default 06:00.</summary>
    [PropertyGroup("Window", 8)]
    public TimeSpan CutOffTime { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Optional JSONPath overriding <see cref="CutOffTime"/>; the value is a time of day in the form
    /// <c>HH:mm</c> or <c>HH:mm:ss</c>.
    /// </summary>
    [PropertyGroup("Window", 9, "jsonpath")]
    public string? CutOffTimePath { get; init; }

    /// <summary>
    /// Length of one allocation slot. Default 15 minutes. Must divide every day of the time zone
    /// without remainder.
    /// </summary>
    [PropertyGroup("Window", 10)]
    public TimeSpan SlotLength { get; init; } = TimeSpan.FromMinutes(15);

    // ------------------------------------------------------------------ Schema

    /// <summary>CkTypeId of the consumer metering points. Default <c>EnergyCommunity/Consumer</c>.</summary>
    [PropertyGroup("Schema", 0, "ckTypeSelector")]
    public string ConsumerCkTypeId { get; init; } = "EnergyCommunity/Consumer";

    /// <summary>CkTypeId of the producer metering points. Default <c>EnergyCommunity/Producer</c>.</summary>
    [PropertyGroup("Schema", 1, "ckTypeSelector")]
    public string ProducerCkTypeId { get; init; } = "EnergyCommunity/Producer";

    /// <summary>
    /// CkTypeId of the base metering point type, used as the target type constraint when navigating
    /// from a measurement anchor or a participation period to its metering point. Default
    /// <c>Basic.Energy/MeteringPoint</c>.
    /// </summary>
    [PropertyGroup("Schema", 2, "ckTypeSelector")]
    public string MeteringPointCkTypeId { get; init; } = "Basic.Energy/MeteringPoint";

    /// <summary>
    /// CkTypeId of the measurement anchors (one per metering point and OBIS code). Default
    /// <c>Basic.Energy/EnergyMeasurement</c>.
    /// </summary>
    [PropertyGroup("Schema", 3, "ckTypeSelector")]
    public string EnergyMeasurementCkTypeId { get; init; } = "Basic.Energy/EnergyMeasurement";

    /// <summary>
    /// Association role from a measurement anchor to its metering point, navigated outbound from the
    /// anchor. Default <c>System/ParentChild</c>.
    /// </summary>
    [PropertyGroup("Schema", 4)]
    public string ParentAssociationRoleId { get; init; } = "System/ParentChild";

    /// <summary>CkTypeId of the participation periods. Default <c>EnergyCommunity/ParticipationPeriod</c>.</summary>
    [PropertyGroup("Schema", 5, "ckTypeSelector")]
    public string ParticipationPeriodCkTypeId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>
    /// Association role from a participation period to its metering point, navigated outbound from
    /// the period. Default <c>EnergyCommunity/ParticipationPeriod</c>.
    /// </summary>
    [PropertyGroup("Schema", 6)]
    public string ParticipationPeriodAssociationRoleId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>
    /// Record attribute on the participation period with <c>From</c> and <c>To</c>; a null
    /// <c>To</c> means open-ended. A metering point participates in a slot when one of its periods
    /// has From not after the slot start and To (if set) not before the slot end. Default
    /// <c>TimeRange</c>.
    /// </summary>
    [PropertyGroup("Schema", 7)]
    public string ParticipationTimeRangeAttribute { get; init; } = "TimeRange";

    /// <summary>
    /// Integer attribute on the metering point with its partition factor in percent (0 to 100).
    /// Default <c>PartitionFactor</c>.
    /// </summary>
    [PropertyGroup("Schema", 8)]
    public string PartitionFactorAttribute { get; init; } = "PartitionFactor";

    /// <summary>Partition factor used when the attribute is missing. Default 100.</summary>
    [PropertyGroup("Schema", 9)]
    public int DefaultPartitionFactor { get; init; } = 100;

    /// <summary>
    /// String attribute on the metering point copied into every output record. Default
    /// <c>MeteringPointNumber</c>.
    /// </summary>
    [PropertyGroup("Schema", 10)]
    public string MeteringPointNumberAttribute { get; init; } = "MeteringPointNumber";

    /// <summary>
    /// Enum attribute on the metering point naming where its data comes from (0 Eda, 1 Simulated,
    /// 2 SelfReported). Default <c>MeteringDataSource</c>.
    /// </summary>
    [PropertyGroup("Schema", 11)]
    public string DataSourceAttribute { get; init; } = "MeteringDataSource";

    /// <summary>
    /// Data source keys the node may allocate for. A day on which any participating metering point
    /// has another data source is aborted. Default 1 (Simulated) and 2 (SelfReported).
    /// </summary>
    [PropertyGroup("Schema", 12)]
    public List<int> AllocatableDataSources { get; init; } = [1, 2];

    /// <summary>
    /// Data source assumed when the attribute is missing. Default 0 (Eda), which is not allocatable,
    /// so a metering point without the attribute aborts its days rather than being overwritten.
    /// </summary>
    [PropertyGroup("Schema", 13)]
    public int DefaultDataSource { get; init; } = 0;

    /// <summary>String attribute on the measurement anchor carrying its OBIS code. Default <c>ObisCode</c>.</summary>
    [PropertyGroup("Schema", 14)]
    public string ObisCodeAttribute { get; init; } = "ObisCode";

    // ------------------------------------------------------------------ Archive

    /// <summary>Runtime id of the time-range archive holding the raw registers.</summary>
    [PropertyGroup("Archive", 0)]
    public required string ArchiveRtId { get; init; }

    /// <summary>Archive column with the slot quantity in kWh. Default <c>Amount.Value</c>.</summary>
    [PropertyGroup("Archive", 1)]
    public string ValueColumn { get; init; } = "Amount.Value";

    /// <summary>
    /// Archive column with the data quality, stored as its enum key (1 L1, 2 L2, 3 L3). Default
    /// <c>DataQuality</c>.
    /// </summary>
    [PropertyGroup("Archive", 2)]
    public string QualityColumn { get; init; } = "DataQuality";

    // ------------------------------------------------------------------ Registers

    /// <summary>OBIS code of the consumer input (total consumption). Default <c>1-1:1.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 0)]
    public string ConsumerInputObis { get; init; } = "1-1:1.9.0 G.01";

    /// <summary>OBIS code of the producer input (total generation). Default <c>1-1:2.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 1)]
    public string ProducerInputObis { get; init; } = "1-1:2.9.0 G.01";

    /// <summary>OBIS code written for the consumer's community share. Default <c>1-1:2.9.0 G.03</c>.</summary>
    [PropertyGroup("Registers", 2)]
    public string ConsumerShareObis { get; init; } = "1-1:2.9.0 G.03";

    /// <summary>
    /// OBIS code written for the generation a producer offers to the community (partition factor
    /// times generation). Default <c>1-1:2.9.0 G.01T</c>.
    /// </summary>
    [PropertyGroup("Registers", 3)]
    public string ProducerOfferedObis { get; init; } = "1-1:2.9.0 G.01T";

    /// <summary>
    /// OBIS code written for the part of the offered generation that was not distributed. Default
    /// <c>1-1:2.9.0 P.01T</c>.
    /// </summary>
    [PropertyGroup("Registers", 4)]
    public string ProducerSurplusObis { get; init; } = "1-1:2.9.0 P.01T";

    // ------------------------------------------------------------------ Output

    /// <summary>
    /// Smallest quantity step in kWh; every output value is a whole multiple of it and every rounding
    /// rounds down. Default 0.001.
    /// </summary>
    [PropertyGroup("Output", 0)]
    public decimal Resolution { get; init; } = 0.001m;

    /// <summary>Unit written into every record. Default <c>kWh</c>, as the EDA ingest writes it.</summary>
    [PropertyGroup("Output", 1)]
    public string QuantityUnit { get; init; } = "kWh";

    /// <summary>
    /// Quality written for a slot whose input value is missing (the value then counts as 0). Default
    /// <c>L3</c>. A present input passes its own quality on: keys 1, 2 and 3 become L1, L2 and L3,
    /// any other key becomes L1.
    /// </summary>
    [PropertyGroup("Output", 2)]
    public string MissingQuality { get; init; } = "L3";

    /// <summary>JSONPath where the list of allocation records is written.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string TargetPath { get; init; }

    /// <summary>Optional JSONPath where the per-day summary list is written.</summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string? SummaryTargetPath { get; init; }
}

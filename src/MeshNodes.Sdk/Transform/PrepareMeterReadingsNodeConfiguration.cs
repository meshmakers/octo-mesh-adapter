using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// PrepareMeterReadings node configuration (AB#5637, requirements MTR-02..04). Validates readings
/// reported for self-reported metering points and turns the accepted values into records that
/// <c>SaveTimeRangeSeriesInArchive@1</c> writes with the configuration of the allocation pipeline,
/// unchanged. The node itself writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Input at <see cref="ReadingsPath"/>: an array of readings, each with <c>meteringPointNumber</c>,
/// <c>direction</c> (<c>consumption</c> or <c>production</c>) and <c>values</c>, an array of
/// <c>from</c>, <c>to</c> (ISO-8601) and <c>kWh</c>. Every value is checked on its own; valid values
/// are accepted even when others are rejected. Per value codes: <c>UNKNOWN_METERING_POINT</c>,
/// <c>NOT_SELF_REPORTED</c>, <c>OFF_GRID</c>, <c>DUPLICATE_SLOT</c>, <c>OUT_OF_RANGE</c>,
/// <c>IN_FUTURE</c>, <c>NOT_PARTICIPATING</c>, <c>DAY_FROZEN</c>.
/// </para>
/// <para>
/// Records at <see cref="TargetPath"/>: one per metering point and register with
/// <c>MeteringPointRtId</c>, <c>MeteringPointNumber</c>, <c>MeterCode</c>, <c>QuantityUnit</c>,
/// <c>CreationTime</c> (now, so a later report wins through <c>SourceDocumentDate</c>),
/// <c>PeriodStart</c>, <c>PeriodEnd</c> and <c>EnergyQuantities</c> (<c>From</c>, <c>To</c>,
/// <c>Quantity</c>, <c>Quality</c>). Result at <see cref="ResultTargetPath"/>: <c>status</c>
/// (<c>ok</c>, <c>partial</c>, <c>error</c>), <c>message</c>, <c>accepted</c>, <c>rejected</c>
/// (<c>reading</c>, <c>value</c>, <c>code</c>, <c>message</c>) and <c>frozenUntil</c>.
/// </para>
/// </remarks>
[NodeName("PrepareMeterReadings", 1)]
public record PrepareMeterReadingsNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    // ------------------------------------------------------------------ Limits

    /// <summary>Maximum number of values in one request; more fails the whole request. Default 2880.</summary>
    [PropertyGroup("Limits", 0)]
    public int MaxValues { get; init; } = 2880;

    /// <summary>Largest accepted quantity of one slot in kWh. Default 1000.</summary>
    [PropertyGroup("Limits", 1)]
    public decimal MaxKWh { get; init; } = 1000m;

    /// <summary>A value whose end lies more than this after now is rejected. Default 1 minute.</summary>
    [PropertyGroup("Limits", 2)]
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Length of one slot; a value must start on this grid and last exactly this long. Default 15 minutes.</summary>
    [PropertyGroup("Limits", 3)]
    public TimeSpan SlotLength { get; init; } = TimeSpan.FromMinutes(15);

    // ------------------------------------------------------------------ Cut-off

    /// <summary>Time zone the reporting days are cut in. Default <c>Europe/Vienna</c>.</summary>
    [PropertyGroup("CutOff", 0)]
    public string TimeZone { get; init; } = "Europe/Vienna";

    /// <summary>
    /// The cut-off of day D is local day D plus this many days at <see cref="CutOffTime"/>, as in
    /// <c>AllocateCommunityEnergy@1</c>. Default 1.
    /// </summary>
    [PropertyGroup("CutOff", 1)]
    public int CutOffDayOffset { get; init; } = 1;

    /// <summary>Local time of day of the cut-off. Default 06:00.</summary>
    [PropertyGroup("CutOff", 2)]
    public TimeSpan CutOffTime { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Optional JSONPath overriding <see cref="CutOffTime"/> (for example
    /// <c>$.communitySettings.Items[0].Attributes.AllocationCutOffTime</c>); the value is
    /// <c>HH:mm</c> or <c>HH:mm:ss</c>, an empty value or a path that resolves to nothing keeps the
    /// configured time.
    /// </summary>
    [PropertyGroup("CutOff", 3, "jsonpath")]
    public string? CutOffTimePath { get; init; }

    // ------------------------------------------------------------------ Schema

    /// <summary>CkTypeId of the consumer metering points. Default <c>EnergyCommunity/Consumer</c>.</summary>
    [PropertyGroup("Schema", 0, "ckTypeSelector")]
    public string ConsumerCkTypeId { get; init; } = "EnergyCommunity/Consumer";

    /// <summary>CkTypeId of the producer metering points. Default <c>EnergyCommunity/Producer</c>.</summary>
    [PropertyGroup("Schema", 1, "ckTypeSelector")]
    public string ProducerCkTypeId { get; init; } = "EnergyCommunity/Producer";

    /// <summary>CkTypeId of the base metering point type. Default <c>Basic.Energy/MeteringPoint</c>.</summary>
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

    /// <summary>Association role from a participation period to its metering point. Default <c>EnergyCommunity/ParticipationPeriod</c>.</summary>
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

    /// <summary>Enum attribute naming where the values come from. Default <c>MeteringDataSource</c>.</summary>
    [PropertyGroup("Schema", 11)]
    public string DataSourceAttribute { get; init; } = "MeteringDataSource";

    /// <summary>Data source assumed when the attribute is missing. Default 0 (Eda).</summary>
    [PropertyGroup("Schema", 12)]
    public int DefaultDataSource { get; init; } = 0;

    /// <summary>The only data source key that may report values. Default 2 (SelfReported).</summary>
    [PropertyGroup("Schema", 13)]
    public int SelfReportedDataSource { get; init; } = 2;

    /// <summary>String attribute on the measurement anchor carrying its OBIS code. Default <c>ObisCode</c>.</summary>
    [PropertyGroup("Schema", 14)]
    public string ObisCodeAttribute { get; init; } = "ObisCode";

    // ------------------------------------------------------------------ Registers / output

    /// <summary>Register written for a consumption reading. Default <c>1-1:1.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 0)]
    public string ConsumerInputObis { get; init; } = "1-1:1.9.0 G.01";

    /// <summary>Register written for a production reading. Default <c>1-1:2.9.0 G.01</c>.</summary>
    [PropertyGroup("Registers", 1)]
    public string ProducerInputObis { get; init; } = "1-1:2.9.0 G.01";

    /// <summary>Quality written with every accepted value. Default <c>L1</c>.</summary>
    [PropertyGroup("Registers", 2)]
    public string Quality { get; init; } = "L1";

    /// <summary>Unit written into every record. Default <c>kWh</c>.</summary>
    [PropertyGroup("Registers", 3)]
    public string QuantityUnit { get; init; } = "kWh";

    // ------------------------------------------------------------------ Paths

    /// <summary>JSONPath to the readings array. Default <c>$.body.readings</c>.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string ReadingsPath { get; init; } = "$.body.readings";

    /// <summary>JSONPath where the records for <c>SaveTimeRangeSeriesInArchive@1</c> are written (an empty array when nothing was accepted).</summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string TargetPath { get; init; }

    /// <summary>JSONPath where the result object is written.</summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public required string ResultTargetPath { get; init; }
}

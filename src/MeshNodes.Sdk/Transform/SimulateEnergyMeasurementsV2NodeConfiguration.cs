using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// SimulateEnergyMeasurements@2 node configuration (AB#5631). Backfills per-15-min-slot archive
/// datapoints for the raw channels of EXISTING <c>EnergyMeasurement</c> anchors, sized per metering
/// point from its own attributes (annual consumption in kWh, PV capacity in kWp, load profile,
/// production type) and steered by revisions of the community's <c>SimulationSettings</c> entity.
/// </summary>
/// <remarks>
/// <para>
/// Differences to version 1 (which stays unchanged): only the raw channels
/// <see cref="ConsumptionObisCode"/> (consumer) and <see cref="ProductionObisCode"/> (producer) are
/// written, never allocation registers; only metering points whose data source marks them as
/// simulated are touched, so EDA and self-reported values are never overwritten; the PV curve is
/// calibrated to a specific annual yield with the solar noon of the site; every random value is
/// derived from the settings seed and the metering point NUMBER, never from an rtId, so two rebuilds
/// with new rtIds produce bit-identical values.
/// </para>
/// <para>
/// Settings revisions are read from the DataContext at <see cref="SettingsPath"/>. The intended
/// wiring is a preceding <c>GetRtEntitiesByType@1</c> with <c>ckTypeId: EnergyCommunity/SimulationSettings</c>
/// and <c>targetPath: $.settings</c>, and <c>settingsPath: $.settings.Items</c> on this node. For a
/// simulated day D the non-superseded revision with the greatest effective date not after D applies;
/// the effective date of every revision except the base revision (smallest <c>EffectiveFrom</c>) is
/// the later of its <c>EffectiveFrom</c> date and its creation date in Europe/Vienna, so a change
/// never acts retroactively.
/// </para>
/// <para>
/// Output is meant to flow straight into <c>SaveTimeRangeStreamDataInArchive@1</c>.
/// </para>
/// </remarks>
[NodeName("SimulateEnergyMeasurements", 2)]
public record SimulateEnergyMeasurementsV2NodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c>.
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    /// <summary>
    /// Inclusive UTC start of the simulation window (the first slot is <c>[StartDate, StartDate +
    /// PT15M)</c>). Optional: a pipeline may instead supply it at execution time via
    /// <see cref="StartDateAttributePath"/>. Exactly one of the two must resolve a value.
    /// </summary>
    [PropertyGroup("Window", 0)]
    public DateTime? StartDate { get; init; }

    /// <summary>
    /// Number of full UTC days to backfill (each day produces 96 slots per simulated anchor).
    /// Optional: may instead be supplied at execution time via <see cref="NumDaysAttributePath"/>.
    /// Exactly one of the two must resolve a value.
    /// </summary>
    [PropertyGroup("Window", 1)]
    public int? NumDays { get; init; }

    /// <summary>
    /// Optional JSONPath into the input DataContext for the start date. When set, it overrides
    /// <see cref="StartDate"/> (e.g. <c>$.startDate</c>).
    /// </summary>
    [PropertyGroup("Window", 2, "jsonpath")]
    public string? StartDateAttributePath { get; init; }

    /// <summary>
    /// Optional JSONPath into the input DataContext for the day count. When set, it overrides
    /// <see cref="NumDays"/> (e.g. <c>$.numDays</c>).
    /// </summary>
    [PropertyGroup("Window", 3, "jsonpath")]
    public string? NumDaysAttributePath { get; init; }

    /// <summary>CkTypeId of the EnergyMeasurement type whose existing anchors are backfilled (e.g. <c>Basic.Energy/EnergyMeasurement</c>).</summary>
    [PropertyGroup("Schema", 0)]
    public required string EnergyMeasurementCkTypeId { get; init; }

    /// <summary>CkRecordId of the TimeRange record on the EnergyMeasurement (e.g. <c>Basic/TimeRange</c>).</summary>
    [PropertyGroup("Schema", 1)]
    public required string TimeRangeCkRecordId { get; init; }

    /// <summary>CkRecordId of the Amount record on the EnergyMeasurement (e.g. <c>Basic/Amount</c>). Carries Value: Double + Unit: Enum.</summary>
    [PropertyGroup("Schema", 2)]
    public required string AmountCkRecordId { get; init; }

    /// <summary>UnitOfMeasure enum value to stamp on each Amount record. Default 1 (KWh in Basic.UnitOfMeasure).</summary>
    [PropertyGroup("Schema", 3)]
    public int AmountUnit { get; init; } = 1;

    /// <summary>
    /// Association role used to navigate from an existing EnergyMeasurement to its parent
    /// MeteringPoint (e.g. <c>System/ParentChild</c>). Anchors without a parent are skipped.
    /// </summary>
    [PropertyGroup("Schema", 4)]
    public required string ParentAssociationRoleId { get; init; }

    /// <summary>
    /// CkTypeId of the (base) MeteringPoint type the parent association points at (e.g.
    /// <c>Basic.Energy/MeteringPoint</c>). Required as the target-type constraint of the
    /// anchor to MeteringPoint navigation; concrete Producer/Consumer subtypes are returned.
    /// </summary>
    [PropertyGroup("Schema", 5)]
    public required string MeteringPointCkTypeId { get; init; }

    /// <summary>
    /// CkTypeId of the producer MeteringPoint type (e.g. <c>EnergyCommunity/Producer</c>). Anchors
    /// whose parent is of this type are producer anchors; every other parent is a consumer.
    /// </summary>
    [PropertyGroup("Schema", 6)]
    public required string ProducerCkTypeId { get; init; }

    /// <summary>
    /// DataQuality enum value to stamp on every generated slot. Default 1 (BasicEnergy/DataQuality.L1, 15-min meter readings).
    /// </summary>
    [PropertyGroup("Schema", 7)]
    public int DataQuality { get; init; } = 1;

    /// <summary>
    /// OBIS code of the raw consumption channel. Only consumer anchors carrying exactly this code
    /// are simulated. Default <c>1-1:1.9.0 G.01</c>.
    /// </summary>
    [PropertyGroup("Channels", 0)]
    public string ConsumptionObisCode { get; init; } = "1-1:1.9.0 G.01";

    /// <summary>
    /// OBIS code of the raw production channel. Only producer anchors carrying exactly this code
    /// are simulated; allocation registers such as <c>1-1:2.9.0 G.03</c>, <c>1-1:2.9.0 G.01T</c> or
    /// <c>1-1:2.9.0 P.01T</c> are never written. Default <c>1-1:2.9.0 G.01</c>.
    /// </summary>
    [PropertyGroup("Channels", 1)]
    public string ProductionObisCode { get; init; } = "1-1:2.9.0 G.01";

    /// <summary>
    /// Attribute name of the metering point number on the parent metering point. The number (not
    /// the rtId) is the identity all deterministic randomness is derived from. Default <c>MeteringPointNumber</c>.
    /// </summary>
    [PropertyGroup("Attributes", 0)]
    public string MeteringPointNumberAttribute { get; init; } = "MeteringPointNumber";

    /// <summary>
    /// Attribute name of the annual consumption in kWh on a consumer metering point. Default <c>EnergyConsumption</c>.
    /// </summary>
    [PropertyGroup("Attributes", 1)]
    public string AnnualConsumptionAttribute { get; init; } = "EnergyConsumption";

    /// <summary>
    /// Attribute name of the installed capacity in kWp on a producer metering point. Default <c>EnergyProductionCapacity</c>.
    /// </summary>
    [PropertyGroup("Attributes", 2)]
    public string ProductionCapacityAttribute { get; init; } = "EnergyProductionCapacity";

    /// <summary>
    /// Attribute name of the production type on a producer metering point (enum, Unknown=0,
    /// Solar=1, HEP=2, Wind=3, Other=4, Biomass=5, CHP=6). Unknown, Solar or a missing value use the
    /// PV curve; every other type is skipped with a warning. Default <c>ProductionType</c>.
    /// </summary>
    [PropertyGroup("Attributes", 3)]
    public string ProductionTypeAttribute { get; init; } = "ProductionType";

    /// <summary>
    /// Attribute name of the load profile on a consumer metering point (enum, H0=0, G0=1, L0=2,
    /// HeatPump=3, Ev=4). HeatPump and Ev fall back to H0 with a warning. Default <c>LoadProfile</c>.
    /// </summary>
    [PropertyGroup("Attributes", 4)]
    public string LoadProfileAttribute { get; init; } = "LoadProfile";

    /// <summary>
    /// Attribute name of the metering data source on the parent metering point (enum, Eda=0,
    /// Simulated=1, SelfReported=2). Default <c>MeteringDataSource</c>.
    /// </summary>
    [PropertyGroup("Attributes", 5)]
    public string DataSourceAttribute { get; init; } = "MeteringDataSource";

    /// <summary>
    /// When true (default), only anchors whose parent metering point carries
    /// <see cref="SimulatedDataSourceValue"/> in <see cref="DataSourceAttribute"/> are simulated;
    /// EDA, self-reported and unmarked metering points are skipped so reported values are never
    /// overwritten. When false, every metering point is simulated.
    /// </summary>
    [PropertyGroup("Attributes", 6)]
    public bool SimulatedOnly { get; init; } = true;

    /// <summary>
    /// Value of <see cref="DataSourceAttribute"/> that marks a simulated metering point. Default 1 (<c>Simulated</c>).
    /// </summary>
    [PropertyGroup("Attributes", 7)]
    public int SimulatedDataSourceValue { get; init; } = 1;

    /// <summary>
    /// Annual consumption in kWh used for consumer metering points without
    /// <see cref="AnnualConsumptionAttribute"/>. Default 3500.
    /// </summary>
    [PropertyGroup("Defaults", 0)]
    public double DefaultConsumerAnnualKWh { get; init; } = 3500;

    /// <summary>
    /// Installed capacity in kWp used for producer metering points without
    /// <see cref="ProductionCapacityAttribute"/>. Default 10.
    /// </summary>
    [PropertyGroup("Defaults", 1)]
    public double DefaultProducerKWp { get; init; } = 10;

    /// <summary>
    /// Load profile (<c>H0</c>, <c>G0</c> or <c>L0</c>) used for consumer metering points without
    /// <see cref="LoadProfileAttribute"/>. Default <c>H0</c>.
    /// </summary>
    [PropertyGroup("Defaults", 2)]
    public string DefaultLoadProfile { get; init; } = "H0";

    /// <summary>
    /// UTC hour (fractional) of the solar noon the PV curve peaks at. Default 11.1667, i.e. 11:10 UTC
    /// for a site at 13 degrees east (Salzburg).
    /// </summary>
    [PropertyGroup("Defaults", 3)]
    public double SolarNoonUtcHour { get; init; } = 11.1667;

    /// <summary>
    /// Optional JSONPath to the settings revisions in the DataContext: either one object or an
    /// array of objects. Accepted shapes, matched case-insensitively: a serialized runtime entity
    /// as written by <c>GetRtEntitiesByType@1</c> (<c>RtId</c>, <c>RtCreationDateTime</c> and the
    /// settings below <c>Attributes</c>), or a flat object such as
    /// <c>{ "seed": 7, "consumptionFactor": 1.2 }</c>. Recommended: <c>$.settings.Items</c> after
    /// <c>GetRtEntitiesByType@1</c> with <c>targetPath: $.settings</c>. Unset or empty means the
    /// simulation defaults (seed 1, factors 1.0, specific yield 1050, spread 0) with a warning.
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string? SettingsPath { get; init; }

    /// <summary>JSONPath where the list of EntityUpdateInfo&lt;RtEntity&gt; datapoints is written.</summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string EntityUpdatesOutputPath { get; init; }

    /// <summary>
    /// Optional JSONPath where a summary object is written: anchors simulated and skipped by
    /// reason, datapoint count, energy in kWh per OBIS code, the settings revisions used with their
    /// effective dates, and the warnings raised.
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string? SummaryOutputPath { get; init; }
}

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// SimulateEnergyMeasurements node configuration. Backfills historical per-15-min-slot archive
/// datapoints for the <c>EnergyMeasurement</c> entities that ALREADY EXIST in the rt-model. The
/// node does NOT create any EnergyMeasurement entities and does NOT emit association updates — it
/// loads the existing EMs of <see cref="EnergyMeasurementCkTypeId"/>, resolves each EM's parent
/// MeteringPoint type to pick a profile (Producer → PV curve, Consumer → load profile), and emits
/// one <c>EntityUpdateInfo&lt;RtEntity&gt;.CreateInsert</c> datapoint per slot keyed by the existing
/// EM's stable <c>RtId</c> / <c>RtWellKnownName</c>. Uses the deterministic BDEW load-profile and PV
/// curve math from <c>EnergyProfiles</c> so the produced amount values are realistic enough to
/// validate chained rollups and time-range archive idempotency.
/// </summary>
/// <remarks>
/// Output is meant to flow straight into <c>SaveTimeRangeStreamDataInArchive@1</c> for the archive
/// write. Because every datapoint carries the rtId of an existing EnergyMeasurement entity, the
/// archive-write integrity guard will accept it. No RT-entity creation / dedup step is involved.
/// </remarks>
[NodeName("SimulateEnergyMeasurements", 1)]
public record SimulateEnergyMeasurementsNodeConfiguration : NodeConfiguration
{
    /// <summary>Inclusive UTC start of the simulation window. The first slot is <c>[StartDate, StartDate + PT15M)</c>.</summary>
    [PropertyGroup("Window", 0)]
    public required DateTime StartDate { get; init; }

    /// <summary>Number of full UTC days to backfill. Each day produces 96 slots per existing EnergyMeasurement.</summary>
    [PropertyGroup("Window", 1)]
    public required int NumDays { get; init; }

    /// <summary>CkTypeId of the EnergyMeasurement type whose existing entities are backfilled (e.g. <c>Basic.Energy/EnergyMeasurement</c>).</summary>
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
    /// MeteringPoint (e.g. <c>System/ParentChild</c>). The parent MeteringPoint type decides the
    /// profile family (PV vs load).
    /// </summary>
    [PropertyGroup("Schema", 4)]
    public required string ParentAssociationRoleId { get; init; }

    /// <summary>
    /// CkTypeId of the (base) MeteringPoint type the parent association points at (e.g.
    /// <c>Basic.Energy/MeteringPoint</c>). Required as the target-type constraint of the
    /// EM → MeteringPoint navigation; concrete Producer/Consumer subtypes are returned.
    /// </summary>
    [PropertyGroup("Schema", 5)]
    public required string MeteringPointCkTypeId { get; init; }

    /// <summary>
    /// CkTypeId of the producer MeteringPoint type (e.g. <c>Basic.Energy/Producer</c>). EMs whose
    /// parent MeteringPoint is of this type use the PV profile; all others use the load profile.
    /// </summary>
    [PropertyGroup("Schema", 6)]
    public required string ProducerCkTypeId { get; init; }

    /// <summary>BDEW load-profile sub-key for consumer EMs (e.g. <c>H0</c> / <c>G0</c> / <c>L0</c>). Default <c>H0</c>.</summary>
    [PropertyGroup("Profile", 0)]
    public string LoadProfileSubKey { get; init; } = "H0";

    /// <summary>Daily energy in kWh for consumer (load-profile) EMs. Default 500.</summary>
    [PropertyGroup("Profile", 1)]
    public double ConsumerDailyKWh { get; init; } = 500;

    /// <summary>Peak power in kWp for producer (PV-profile) EMs. Default 50.</summary>
    [PropertyGroup("Profile", 2)]
    public double ProducerPeakKWp { get; init; } = 50;

    /// <summary>JSONPath where the list of EntityUpdateInfo&lt;RtEntity&gt; datapoints is written.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string EntityUpdatesOutputPath { get; init; }

    /// <summary>
    /// DataQuality enum value to stamp on every generated slot. Default 1 (BasicEnergy/DataQuality.L1 — 15-min meter readings).
    /// </summary>
    [PropertyGroup("Schema", 6)]
    public int DataQuality { get; init; } = 1;
}

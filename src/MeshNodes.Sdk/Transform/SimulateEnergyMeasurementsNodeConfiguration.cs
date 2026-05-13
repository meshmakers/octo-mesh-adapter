using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// SimulateEnergyMeasurements node configuration. Generates per-15-min-slot
/// <c>EnergyMeasurement</c> Insert candidates (and their parent <c>ParentChild</c> association
/// candidates) for a configured set of MeteringPoints over a configured time window. Uses BDEW
/// H0/G0/L0 load-profile or PV-curve math (deterministic, no calendar / season effects beyond
/// the PV day-length cosine) so the produced amount values are realistic enough to validate
/// chained rollups and time-range archive idempotency.
/// </summary>
/// <remarks>
/// Output is meant to flow into <c>UpdateRtEntityIfNewer@1</c> for Lesart-D dedup, then
/// <c>ApplyChanges@2</c> for the RT-write, and <c>SaveTimeRangeStreamDataInArchive@1</c> for
/// the archive write. The natural key for the dedup is <c>RtWellKnownName = "EM-{mpRtId}-{obisCode}"</c>.
/// </remarks>
[NodeName("SimulateEnergyMeasurements", 1)]
public record SimulateEnergyMeasurementsNodeConfiguration : NodeConfiguration
{
    /// <summary>Inclusive UTC start of the simulation window. The first slot is <c>[StartDate, StartDate + PT15M)</c>.</summary>
    [PropertyGroup("Window", 0)]
    public required DateTime StartDate { get; init; }

    /// <summary>Number of full UTC days to simulate. Each day produces 96 slots per MeteringPoint × ObisCode pair.</summary>
    [PropertyGroup("Window", 1)]
    public required int NumDays { get; init; }

    /// <summary>CkTypeId of the EnergyMeasurement type the simulator emits (e.g. <c>Basic.Energy/EnergyMeasurement</c>).</summary>
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

    /// <summary>Association role used to wire each new EnergyMeasurement to its parent MeteringPoint (e.g. <c>System/ParentChild</c>).</summary>
    [PropertyGroup("Schema", 4)]
    public required string ParentAssociationRoleId { get; init; }

    /// <summary>JSONPath where the list of EntityUpdateInfo&lt;RtEntity&gt; candidates is written.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string EntityUpdatesOutputPath { get; init; }

    /// <summary>JSONPath where the list of AssociationUpdateInfo candidates is written.</summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string AssociationUpdatesOutputPath { get; init; }

    /// <summary>The MeteringPoints to simulate against. At least one entry required.</summary>
    [PropertyGroup("MeteringPoints", 0)]
    public required ICollection<MeteringPointSimDefinition> MeteringPoints { get; init; }

    /// <summary>
    /// DataQuality enum value to stamp on every generated slot. Default 1 (BasicEnergy/DataQuality.L1 — 15-min meter readings).
    /// </summary>
    [PropertyGroup("Schema", 5)]
    public int DataQuality { get; init; } = 1;
}

/// <summary>
/// One MeteringPoint definition for the simulator. Each entry generates <c>NumDays * 96 *
/// ObisCodes.Count</c> slots with deterministic amounts driven by <see cref="ProfileKind"/>
/// and <see cref="ProfileParameter"/>.
/// </summary>
public class MeteringPointSimDefinition
{
    /// <summary>Runtime id of the existing MeteringPoint entity.</summary>
    public required string MeteringPointRtId { get; init; }

    /// <summary>CkTypeId of the MeteringPoint (e.g. <c>Basic.Energy/Consumer</c> or <c>Basic.Energy/Producer</c>).</summary>
    public required string MeteringPointCkTypeId { get; init; }

    /// <summary>
    /// Profile to apply: <c>Load:H0</c> / <c>Load:G0</c> / <c>Load:L0</c> for consumers (parameter =
    /// daily energy in kWh), or <c>PV</c> for producers (parameter = peak power in kWp).
    /// </summary>
    public required string ProfileKind { get; init; }

    /// <summary>Profile parameter — daily kWh for load profiles, peak kWp for PV.</summary>
    public required double ProfileParameter { get; init; }

    /// <summary>OBIS codes to produce slots for. Each becomes its own EnergyMeasurement series.</summary>
    public required ICollection<string> ObisCodes { get; init; }
}

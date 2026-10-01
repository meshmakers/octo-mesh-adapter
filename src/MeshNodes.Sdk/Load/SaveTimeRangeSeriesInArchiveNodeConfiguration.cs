using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Which object a <see cref="TimeRangeSeriesColumn" /> reads its value from.
/// </summary>
public enum TimeRangeSeriesColumnScope
{
    /// <summary>The individual windowed value (the default) — e.g. the quantity measured in that window.</summary>
    Value = 0,

    /// <summary>The series the value belongs to — e.g. a unit or register code that is constant for the whole series.</summary>
    Series = 1
}

/// <summary>
/// One archive column and where its value comes from.
/// </summary>
public record TimeRangeSeriesColumn
{
    /// <summary>
    /// Target attribute path on the archived entity, exactly as the archive declares it in
    /// <c>Columns[].Path</c> (e.g. <c>Amount.Value</c>). Required.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Property name on the source object named by <see cref="Scope" />. A plain property name, not
    /// a JSONPath — this node reads the parsed document directly instead of evaluating a path
    /// expression per value, which is the whole point of it. Required.
    /// </summary>
    public required string ValueProperty { get; init; }

    /// <summary>Which object <see cref="ValueProperty" /> is read from. Defaults to the value.</summary>
    public TimeRangeSeriesColumnScope Scope { get; init; } = TimeRangeSeriesColumnScope.Value;
}

/// <summary>
/// SaveTimeRangeSeriesInArchive node configuration. Ingests whole <em>series</em> of windowed
/// measurements — each series being one anchor entity plus its list of <c>[from, to)</c> values —
/// into a <c>TimeRangeArchive</c>, in a single node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The composition it replaces (<c>ForEach</c> over the values →
/// <c>CreateUpdateInfo@1</c> + <c>CreateAssociationUpdate@1</c> per value → <c>Flatten</c> →
/// <c>UpdateRtEntityIfNewer@1</c> → <c>ApplyChanges@2</c> → <c>SaveTimeRangeStreamDataInArchive@1</c>)
/// materialises one full runtime entity per measured window in order to write one time-series row.
/// Measured on a real EDA replay that is ~12.8 ms and roughly two dozen node executions per
/// 15-minute slot, and 316,268 slots took 75 minutes — with the database nowhere near the
/// bottleneck. The runtime model only ever needs <em>one</em> anchor entity per series, so the
/// per-value entity round trip is pure overhead.
/// </para>
/// <para>
/// <b>What it does.</b> Resolves each series' anchor entity by <see cref="WellKnownNameFormat" />
/// (one batched query for all series), creates the ones that do not exist yet together with their
/// parent association, advances the anchor's own attributes to the series' newest value, persists
/// that small set of entities, and only then writes every windowed value to the archive in one
/// bulk insert. The anchor write happens first on purpose, and that order is what keeps the archive
/// free of rows whose source entity does not exist: every RtId handed to the archive was either read
/// from the runtime store or written and confirmed, and a failed anchor write throws instead of
/// continuing. It is an invariant of this node, not a check performed afterwards — the post-hoc
/// orphan guard in <c>SaveTimeRangeStreamDataInArchive@1</c> does not apply here.
/// </para>
/// <para>
/// <b>Ordering.</b> This node does not decide which of two deliveries for the same window wins —
/// the archive does, via its opt-in <c>ConflictPrecedence</c> (System.StreamData 1.13.0), an ordered
/// list of keys compared lexicographically. Map the columns that rank a delivery — a quality code,
/// the source document's own date — into the archive via <see cref="Columns" />, declare them as the
/// archive's precedence keys, and the surviving value is the same whichever write arrives first.
/// Writes that are equal in every key are not ordered; the row stored first stays.
/// </para>
/// </remarks>
[NodeName("SaveTimeRangeSeriesInArchive", 1)]
public record SaveTimeRangeSeriesInArchiveNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c>, or <c>System</c>.
    /// A missing value resolves to <c>Caller</c> (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    /// <summary>
    /// Runtime id of the target <c>TimeRangeArchive</c>. Must exist, be activated, and declare a
    /// column for every entry in <see cref="Columns" />. Required.
    /// </summary>
    [PropertyGroup("Archive", 0)]
    public required string ArchiveRtId { get; init; }

    /// <summary>
    /// CK type of the anchor entity. Must match the archive's <c>TargetCkTypeId</c>, otherwise the
    /// repository silently drops every row. Required.
    /// </summary>
    [PropertyGroup("Archive", 1)]
    public required string CkTypeId { get; init; }

    /// <summary>
    /// Property name on a series object holding its array of windowed values. Required.
    /// </summary>
    [PropertyGroup("Series", 0)]
    public required string ValuesProperty { get; init; }

    /// <summary>
    /// Template for the anchor's <c>RtWellKnownName</c>, with <c>{PropertyName}</c> placeholders
    /// resolved against the series object — e.g. <c>{MeteringPointRtId}_{MeterCode}</c>. This is the
    /// natural key that decides which series share an anchor, so it must be stable across runs.
    /// Required.
    /// </summary>
    [PropertyGroup("Series", 1)]
    public required string WellKnownNameFormat { get; init; }

    /// <summary>Property name on a value holding the inclusive window start. Required.</summary>
    [PropertyGroup("Window", 0)]
    public required string FromProperty { get; init; }

    /// <summary>Property name on a value holding the exclusive window end. Required.</summary>
    [PropertyGroup("Window", 1)]
    public required string ToProperty { get; init; }

    /// <summary>
    /// The archive columns and where each one's value comes from. Required and non-empty: a series
    /// write with no columns would store nothing but window boundaries.
    /// </summary>
    /// <remarks>
    /// A concrete <see cref="List{T}" /> on purpose. Pipeline definitions are deserialized by
    /// YamlDotNet, which has no node deserializer for <c>IReadOnlyList&lt;T&gt;</c> — a config
    /// declaring one compiles, unit-tests fine (C# constructs it directly) and then fails at
    /// registration with "No node deserializer was able to deserialize the node into type
    /// IReadOnlyList`1[...]". Every other node config in this assembly uses
    /// <c>ICollection&lt;T&gt;</c> or <c>List&lt;T&gt;</c>.
    /// </remarks>
    [PropertyGroup("Columns", 0)]
    public required List<TimeRangeSeriesColumn> Columns { get; init; }

    /// <summary>
    /// Attribute path on the anchor entity that receives the winning value's window start (e.g.
    /// <c>TimeRange.From</c>). Optional — when unset the anchor carries only the mapped
    /// <see cref="Columns" />.
    /// </summary>
    [PropertyGroup("Anchor", 0)]
    public string? AnchorWindowFromAttribute { get; init; }

    /// <summary>
    /// Attribute path on the anchor entity that receives the winning value's window end (e.g.
    /// <c>TimeRange.To</c>). Optional, but it is also the tie-breaker: when set, a series only
    /// advances its existing anchor if its newest window ends later than the one already stored, so
    /// a back-filled or re-delivered older series cannot regress the anchor. When unset the anchor
    /// is always advanced to the series' last value in document order.
    /// </summary>
    [PropertyGroup("Anchor", 1)]
    public string? AnchorWindowToAttribute { get; init; }

    /// <summary>
    /// Property name on the series object holding the RtId of the parent the anchor is associated
    /// to. Optional — when unset no association is created. When set,
    /// <see cref="ParentCkTypeId" /> and <see cref="ParentAssociationRoleId" /> are required.
    /// </summary>
    [PropertyGroup("Parent", 0)]
    public string? ParentRtIdProperty { get; init; }

    /// <summary>CK type of the parent entity. Required when <see cref="ParentRtIdProperty" /> is set.</summary>
    [PropertyGroup("Parent", 1)]
    public string? ParentCkTypeId { get; init; }

    /// <summary>Association role linking anchor to parent. Required when <see cref="ParentRtIdProperty" /> is set.</summary>
    [PropertyGroup("Parent", 2)]
    public string? ParentAssociationRoleId { get; init; }
}

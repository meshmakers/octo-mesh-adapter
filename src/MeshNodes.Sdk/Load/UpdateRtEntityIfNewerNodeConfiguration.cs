using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// UpdateRtEntityIfNewer node configuration. Filters a list of <c>EntityUpdateInfo</c> candidates
/// by comparing each candidate against the existing RT entity that shares the same
/// <c>RtWellKnownName</c>. Older candidates (whose <see cref="ComparisonAttributePath"/> is not
/// strictly greater than the existing entity's value) are skipped from the RT-write path but kept
/// — with the existing RtId — on the all-output path so a downstream archive write can still land
/// the corrected slot in storage.
/// </summary>
/// <remarks>
/// <para>
/// This node implements the Lesart-D semantics of the time-range-archive simulation: the RT entity
/// holds the *most recent* measured value (per (parent, ObisCode) pair, identified by
/// <c>RtWellKnownName</c>); the archive holds the full back-fillable time series. Late-arriving
/// or back-filled slots must not regress the RT snapshot but must still be persisted in the archive.
/// </para>
/// <para>
/// Behaviour per input entry (carries an <c>EntityModOptions.Insert</c> with a freshly generated
/// RtId; <c>RtWellKnownName</c> is required for the dedup):
/// <list type="bullet">
/// <item><description>No matching existing entity ⇒ pass through as <c>Insert</c> on both outputs.</description></item>
/// <item><description>Existing entity, candidate's comparison value &gt; existing's ⇒ rewrite as <c>Update</c> targeting the existing RtId on both outputs.</description></item>
/// <item><description>Existing entity, candidate's comparison value ≤ existing's ⇒ omitted from
///   <see cref="FilteredOutputPath"/>; emitted on <see cref="OutputPathAll"/> as an <c>Update</c>
///   carrying the existing RtId and the candidate's attributes (so the archive write sees the
///   corrected slot).</description></item>
/// <item><description>Input entries without an <c>RtWellKnownName</c> are passed through as Insert
///   on both outputs (the dedup is structurally impossible).</description></item>
/// </list>
/// </para>
/// <para>
/// Race window: the lookup is done in a separate session from the subsequent <c>ApplyChanges@2</c>
/// write. A concurrent writer between the two would not be detected. Acceptable for the simulation;
/// not a production-grade dedup.
/// </para>
/// </remarks>
[NodeName("UpdateRtEntityIfNewer", 1)]
public record UpdateRtEntityIfNewerNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// JSONPath to the input <c>List&lt;EntityUpdateInfo&lt;RtEntity&gt;&gt;</c> of candidate
    /// updates. Required.
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public required string InputPath { get; init; }

    /// <summary>
    /// JSONPath where the filtered list is written. Contains only entries that should reach the
    /// downstream <c>ApplyChanges@2</c> (Insert + Update with strictly newer comparison value).
    /// Required.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string FilteredOutputPath { get; init; }

    /// <summary>
    /// JSONPath where the complete list is written, including skipped entries (with the existing
    /// RtId substituted in). Consumed by downstream archive-write nodes that must store every slot
    /// even when the RT snapshot is not advanced. Required.
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public required string OutputPathAll { get; init; }

    /// <summary>
    /// Attribute path on the candidate's <c>RtEntity.Attributes</c> dictionary that holds the
    /// monotonic comparison value (typically a UTC <c>DateTime</c>). Supports nested traversal of
    /// record-typed attributes via dot notation, e.g. <c>"TimeRange.From"</c>. Required.
    /// </summary>
    [PropertyGroup("Comparison", 0)]
    public required string ComparisonAttributePath { get; init; }

    /// <summary>
    /// JSONPath to an input <c>List&lt;AssociationUpdateInfo&gt;</c> of candidate parent
    /// associations (one per candidate entity, origin pointing at the candidate's freshly-generated
    /// RtId). Optional — when set, the node also writes a filtered association list at
    /// <see cref="FilteredAssociationsOutputPath"/> that contains only the associations whose
    /// origin entity stayed an <c>Insert</c> in the filtered output. Associations whose origin was
    /// rewritten to <c>Update</c> or dropped (SKIP) are removed, since the existing entity already
    /// has its parent association in the runtime store.
    /// </summary>
    [PropertyGroup("Associations", 0, "jsonpath")]
    public string? CandidateAssociationsInputPath { get; init; }

    /// <summary>
    /// JSONPath where the filtered association list is written. Required when
    /// <see cref="CandidateAssociationsInputPath"/> is set.
    /// </summary>
    [PropertyGroup("Associations", 1, "jsonpath")]
    public string? FilteredAssociationsOutputPath { get; init; }
}

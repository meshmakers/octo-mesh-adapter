using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// SaveTimeRangeStreamDataInArchive node configuration. Writes externally pre-aggregated
/// time-range data points into a <c>TimeRangeArchive</c> identified by <see cref="ArchiveRtId"/>
/// (time-range concept §3, §6). The upstream entity must carry the window boundaries — by
/// default in top-level attributes named <c>From</c> / <c>To</c>; configurable via
/// <see cref="FromAttributePath"/> and <see cref="ToAttributePath"/>.
/// </summary>
/// <remarks>
/// Sibling of <see cref="SaveStreamDataInArchiveNodeConfiguration"/> for the time-range
/// storage shape. Re-deliveries of the same <c>(from, to, rtid, ckTypeId)</c> upsert in
/// CrateDB and flip the <c>was_updated</c> flag to true (concept §5). The target archive must
/// be a <c>TimeRangeArchive</c> in <c>Activated</c> status; raw or rollup archives are
/// rejected by the repository with a clear error.
/// </remarks>
[NodeName("SaveTimeRangeStreamDataInArchive", 1)]
public record SaveTimeRangeStreamDataInArchiveNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the target <c>TimeRangeArchive</c>. The archive must exist in the tenant,
    /// be activated, and have a column set that covers the attributes the upstream entities
    /// carry (unknown attributes are dropped server-side). Required.
    /// </summary>
    public required string ArchiveRtId { get; init; }

    /// <summary>
    /// Attribute name on the upstream entity that carries the inclusive window start (UTC
    /// DateTime). Default <c>"From"</c>. The attribute is removed from the per-row attribute
    /// dictionary so it doesn't appear as a user column.
    /// </summary>
    public string FromAttributePath { get; init; } = "From";

    /// <summary>
    /// Attribute name on the upstream entity that carries the exclusive window end (UTC
    /// DateTime). Default <c>"To"</c>. Same removal semantics as <see cref="FromAttributePath"/>.
    /// </summary>
    public string ToAttributePath { get; init; } = "To";

    /// <summary>
    /// Origin of the rows this node writes (AB#7596): <see cref="ArchiveWriteOrigin.Automated" /> (default, the
    /// behaviour before the option existed) or <see cref="ArchiveWriteOrigin.Manual" />. A manual row records
    /// its writer (the verified user caller, else the client id of the pipeline's service identity) and the
    /// platform time. An archive that ranks by <c>$Origin</c> keeps a manual value against
    /// every later automated delivery.
    /// </summary>
    public ArchiveWriteOrigin Origin { get; init; } = ArchiveWriteOrigin.Automated;

    /// <summary>
    /// Optional reason recorded with every manual row of this node (AB#7593). Only allowed with
    /// <see cref="Origin" /> <see cref="ArchiveWriteOrigin.Manual" />.
    /// </summary>
    public string? ManualReason { get; init; }

    /// <summary>
    /// Optional attribute on the upstream entity that carries the reason of a manual row (AB#7593), for a
    /// reason that differs per row. Removed from the attributes like the window attributes; takes precedence
    /// over <see cref="ManualReason" /> when present and not empty. Only allowed with
    /// <see cref="Origin" /> <see cref="ArchiveWriteOrigin.Manual" />.
    /// </summary>
    public string? ManualReasonAttributePath { get; init; }

    /// <summary>
    /// Optional path, relative to each item at <see cref="Path" />, that carries the reason of a manual row
    /// (AB#7638), for example <c>$.manualReason</c>. The value is read from the DataContext item next to the
    /// update info, so the CK type needs no reason attribute and nothing surfaces as a user column. Takes
    /// precedence over <see cref="ManualReasonAttributePath" /> and <see cref="ManualReason" /> when the item
    /// carries a non-empty string there. Only allowed with <see cref="Origin" /> <see cref="ArchiveWriteOrigin.Manual" />.
    /// </summary>
    public string? ManualReasonPath { get; init; }

    /// <summary>
    /// Optional path the write result is stored at (AB#7598): <c>{ stored, skipped, skippedByReason }</c>, so a
    /// later node can branch on it. Rows that lose against a stored row are a normal outcome and do not fail the
    /// node unless <see cref="FailOnSkippedRows" /> is set.
    /// </summary>
    public string? WriteResultPath { get; init; }

    /// <summary>
    /// Fails the node when the archive did not store every row (AB#7598), for any reason including
    /// <c>CkTypeMismatch</c>, for example an automated delivery against a manual value or a lower quality against
    /// a higher one. Default <c>false</c>: automated rows not stored only for <c>CkTypeMismatch</c> are logged at
    /// Debug (AB#7988, expected when one update list feeds one node per archive); any other reason is reported as a
    /// warning. A manual row that could not be stored always fails the node.
    /// </summary>
    public bool FailOnSkippedRows { get; init; }
}

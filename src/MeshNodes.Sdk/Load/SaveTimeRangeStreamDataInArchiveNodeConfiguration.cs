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
}

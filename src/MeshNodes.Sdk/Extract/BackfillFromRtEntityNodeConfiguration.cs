using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Backfills missing attributes on a list of <c>EntityUpdateInfo&lt;RtEntity&gt;</c> items from
/// each item's persistent counterpart in MongoDB, using the target archive's column spec to
/// decide which attributes are needed.
/// </summary>
/// <remarks>
/// Designed to sit immediately before <c>SaveStreamDataInArchive</c> in event-sourced pipelines
/// (e.g. Loxone state polling) where each upstream event only carries one attribute. The
/// archive's <c>Columns</c> declare the per-row schema; for every item, attributes that are not
/// already set on the in-flight update are loaded from the persistent <c>RtEntity</c> by its own
/// <c>RtId</c>. The result is a complete row snapshot that satisfies any <c>Required</c>/NOT NULL
/// columns on the per-archive CrateDB table.
/// </remarks>
[NodeName("BackfillFromRtEntity", 1)]
public record BackfillFromRtEntityNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the target <c>CkArchive</c> entity. The archive's <c>Columns</c> list is
    /// the schema that drives backfill — every column path that is not yet populated on an
    /// update item is loaded from the persistent entity. Required.
    /// </summary>
    public required string ArchiveRtId { get; init; }
}

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// SaveInTimeSeries node configuration. Routes the source entities into a single archive
/// (per-node — no auto fan-out) identified by <see cref="ArchiveRtId"/>.
/// </summary>
/// <remarks>
/// Concept §6 / §8 T9: each pipeline node targets exactly one CkArchive. The archive must be in
/// status <c>Activated</c> at runtime; otherwise the underlying repository call throws
/// <c>ArchiveNotActivatedException</c> and the pipeline surface that as a node-level error.
/// </remarks>
[NodeName("SaveInTimeSeries", 2)]
public record SaveInTimeSeriesNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the <c>CkArchive</c> entity that receives the data points produced by this
    /// node. Must reference an archive that exists in the tenant and is activated. Required.
    /// </summary>
    public required string ArchiveRtId { get; init; }
}

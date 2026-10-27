using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// SaveStreamDataInArchive node configuration. Routes the source entities into a single archive
/// (per-node — no auto fan-out) identified by <see cref="ArchiveRtId"/>.
/// </summary>
/// <remarks>
/// Concept §6 / §8 T9: each pipeline node targets exactly one CkArchive. The archive must be in
/// status <c>Activated</c> at runtime; otherwise the underlying repository call throws
/// <c>ArchiveNotActivatedException</c> and the pipeline surface that as a node-level error.
/// </remarks>
[NodeName("SaveStreamDataInArchive", 1)]
public record SaveStreamDataInArchiveNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Runtime id of the <c>CkArchive</c> entity that receives the data points produced by this
    /// node. Must reference an archive that exists in the tenant and is activated. Required.
    /// </summary>
    public required string ArchiveRtId { get; init; }

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
}

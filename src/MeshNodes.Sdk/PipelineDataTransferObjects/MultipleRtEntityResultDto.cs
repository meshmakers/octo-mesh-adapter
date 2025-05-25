using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// Represents the result of a multiple RT entity extraction operation.
/// </summary>
public record MultipleRtEntityResultDto
{
    /// <summary>
    /// Represents the origin RtId for the entity extraction operation.
    /// </summary>
    public required OctoObjectId OriginRtId { get; init; }

    /// <summary>
    /// Returns the total count of entities extracted during the operation.
    /// </summary>
    public required long TotalCount { get; init; }

    /// <summary>
    /// Represents the collection of entities extracted during the operation.
    /// </summary>
    public required IEnumerable<RtEntity> Items { get; init; } = new List<RtEntity>();
}
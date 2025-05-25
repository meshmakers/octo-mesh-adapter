using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node get query by id
/// </summary>
[NodeName("GetQueryById", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetQueryByIdNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Gets or sets the query rt id
    /// </summary>
    public required OctoObjectId QueryRtId { get; init; }

    /// <summary>
    /// Amount of rows to skip
    /// </summary>
    public int Skip { get; init; } = 0;

    /// <summary>
    /// Amount of rows to take
    /// </summary>
    public int Take { get; init; } = 10;

    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
}
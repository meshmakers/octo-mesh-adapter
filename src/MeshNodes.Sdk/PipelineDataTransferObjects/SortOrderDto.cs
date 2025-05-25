using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// Allows defining a sort order for a query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record SortOrderDto
{
    /// <summary>
    /// The sort order
    /// </summary>
    public required SortOrdersDto SortOrder { get; init; }
    /// <summary>
    /// The attribute name
    /// </summary>
    public required string AttributeName { get; set; }
}
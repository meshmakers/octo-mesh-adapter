using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// One aggregation to compute over a time range: which column, and how to condense it.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record AggregationColumnDto
{
    /// <summary>
    /// Attribute path of the archive column to aggregate (e.g. "Energy", "Amount.Value").
    /// </summary>
    public required string AttributePath { get; set; }

    /// <summary>
    /// The aggregation to apply: Count, Minimum, Maximum, Average or Sum.
    /// </summary>
    public required AggregationTypesDto Function { get; set; }
}

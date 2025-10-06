using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// Represents a filter for a field with a specific path in the data structure.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record FieldFilterWithPathDto
{
    /// <summary>Path of the attribute to filter</summary>
    public required string AttributePath { get; set; }

    /// <summary>Comparison operator</summary>
    public FieldFilterOperatorDto Operator { get; set; }

    /// <summary>
    /// Comparison value for the filter. This can be a primitive type, a complex object, or null.
    /// </summary>
    public object? ComparisonValue { get; set; }

    /// <summary>
    /// Path to the value to compare against, if applicable.
    /// </summary>
    public string? ComparisonValuePath { get; set; }
}
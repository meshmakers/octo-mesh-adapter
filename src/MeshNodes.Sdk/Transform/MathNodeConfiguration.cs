using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Represents a configuration for a math node that performs mathematical operations on data.
/// </summary>
[NodeName("Math", 1)]
public record MathNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Specifies the mathematical operation to be performed on the data.
    /// </summary>
    public required MathOperationDto Operation { get; init; }

    /// <summary>
    /// The second value to be used in the mathematical operation.
    /// </summary>
    public double? Value { get; init; }

    /// <summary>
    /// The path to the value to be used in the mathematical operation.
    /// </summary>
    public string? ValuePath { get; init; }

    /// <summary>
    /// Relative path to the source objects where the value to be processed is located.
    /// </summary>
    public required string ItemPath { get; init; }

    /// <summary>
    /// Relative path to the source objects where the result of the operation will be stored.
    /// </summary>
    public required string ItemTargetPath { get; init; } = "$.Result";

}
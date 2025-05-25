namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// Represents a mathematical operation that can be performed on data.
/// </summary>
public enum MathOperationDto
{
    /// <summary>
    /// Specifies a multiplication operation.
    /// </summary>
    Multiply = 0,

    /// <summary>
    /// Specifies a division operation.
    /// </summary>
    Divide = 1,

    /// <summary>
    /// Specifies an addition operation.
    /// </summary>
    Add = 2,

    /// <summary>
    /// Specifies a subtraction operation.
    /// </summary>
    Subtract = 3,
}
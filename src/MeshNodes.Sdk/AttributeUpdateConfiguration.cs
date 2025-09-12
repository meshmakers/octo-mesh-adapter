using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.MeshAdapter.Nodes;

/// <summary>
/// Update of a specific attribute
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
[DebuggerDisplay("AttributeName = {AttributeName}, ValuePath = {ValuePath}, Value = {Value}")]
public class AttributeUpdateConfiguration
{
    /// <summary>
    /// Defines the attribute name path
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// Gets or sets the attribute value type
    /// </summary>
    public AttributeValueTypesDto? AttributeValueType { get; set; }

    /// <summary>
    /// Value path
    /// </summary>
    public string? ValuePath { get; set; }
    
    /// <summary>
    /// Optionally constant value
    /// </summary>
    public object? Value { get; set; }

}
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the UpdateRecordArrayItem node.
/// Finds a record in a RecordArray by matching a key attribute, then updates
/// specified attributes on that record. If the record is not found, it is skipped.
/// </summary>
[NodeName("UpdateRecordArrayItem", 1)]
public record UpdateRecordArrayItemNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the record attribute to match against (e.g. "Name" or "ExternalId").
    /// </summary>
    [PropertyGroup("Match", 0)]
    public required string MatchAttributeName { get; set; }

    /// <summary>
    /// Value to match against the MatchAttributeName. Static value.
    /// </summary>
    [PropertyGroup("Match", 1)]
    public string? MatchValue { get; set; }

    /// <summary>
    /// JSONPath to the value to match against. Takes precedence over MatchValue if both are set.
    /// </summary>
    [PropertyGroup("Match", 2, "jsonpath")]
    public string? MatchValuePath { get; set; }

    /// <summary>
    /// List of attribute updates to apply to the matched record.
    /// </summary>
    [PropertyGroup("Updates", 0)]
    public required ICollection<RecordAttributeUpdate> AttributeUpdates { get; set; }
}

/// <summary>
/// Defines a single attribute update on a record.
/// </summary>
public record RecordAttributeUpdate
{
    /// <summary>
    /// Name of the record attribute to update.
    /// </summary>
    public required string AttributeName { get; set; }

    /// <summary>
    /// Static value to set.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// JSONPath to the value to set. Takes precedence over Value.
    /// </summary>
    [PropertyGroup("", 0, "jsonpath")]
    public string? ValuePath { get; set; }
}

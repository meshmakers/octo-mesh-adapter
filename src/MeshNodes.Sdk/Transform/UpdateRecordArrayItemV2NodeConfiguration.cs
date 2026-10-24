using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the UpdateRecordArrayItem@2 node.
/// Updates several records of one RecordArray in a single pass: every item of the array at
/// <c>UpdatesPath</c> addresses one record (by the value at <c>ItemMatchValuePath</c>, relative to that
/// item) and carries the new attribute values (<c>ValuePath</c> relative to that item). The whole
/// modified array is written once to <c>TargetPath</c>. Updates whose key matches no record are skipped.
/// </summary>
[NodeName("UpdateRecordArrayItem", 2)]
public record UpdateRecordArrayItemV2NodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// JSONPath to the array of updates. Every item addresses one record of the array at <c>Path</c>.
    /// Several items for the same record are applied in array order (last value wins per attribute).
    /// </summary>
    [PropertyGroup("Updates", 0, "jsonpath")]
    public required string UpdatesPath { get; set; }

    /// <summary>
    /// Name of the record attribute to match against (e.g. "ExternalId"). The comparison is
    /// case-insensitive on the value; the attribute key may be PascalCase or camelCase in the stored record.
    /// </summary>
    [PropertyGroup("Match", 0)]
    public required string MatchAttributeName { get; set; }

    /// <summary>
    /// JSONPath, relative to each update item (e.g. <c>$.pollUuid</c>), to the value that selects the record.
    /// </summary>
    [PropertyGroup("Match", 1, "jsonpath")]
    public required string ItemMatchValuePath { get; set; }

    /// <summary>
    /// Attribute updates applied to every addressed record. <c>ValuePath</c> is relative to the update
    /// item; <c>Value</c> is a static value, <c>=now()</c> sets the current UTC time.
    /// </summary>
    [PropertyGroup("Updates", 1)]
    public required ICollection<RecordAttributeUpdate> AttributeUpdates { get; set; }
}

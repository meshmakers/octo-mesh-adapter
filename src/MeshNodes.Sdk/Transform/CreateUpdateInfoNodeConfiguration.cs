using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for update a rt entity object
/// </summary>
[NodeName("CreateUpdateInfo", 1)]
public record CreateUpdateInfoNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Kind of update
    /// </summary>
    [PropertyGroup("Options", 0)]
    public UpdateKind? UpdateKind { get; set; }

    /// <summary>
    /// The path to the update kind
    /// </summary>
    [PropertyGroup("Options", 1, "jsonpath")]
    public string? UpdateKindPath { get; set; }

    /// <summary>
    /// The path to the RtEntityId
    /// </summary>
    [PropertyGroup("Entity", 0, "jsonpath")]
    public string? RtIdPath { get; set; }

    /// <summary>
    /// The runtime id of the object
    /// </summary>
    [PropertyGroup("Entity", 1)]
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// When true, the RtId will be generated if it is not existing when RtIdPath is set
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public bool GenerateRtId { get; set; } = false;

    /// <summary>
    /// The runtime construction kit type id (use either this or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 3, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the JSON path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 4, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Jsonpath to the timestamp property if available
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string? TimestampPath { get; set; }

    /// <summary>
    /// Updates to the RtEntity attributes
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public ICollection<AttributeUpdateConfiguration>? AttributeUpdates { get; set; }

    /// <summary>
    /// Atomic updates of single items of RecordArray attributes (AB#7442). Each entry changes the record whose
    /// key attribute equals the match value, in ONE atomic database update, without reading the entity first.
    /// Only for <c>UpdateKind: UPDATE</c>. Use it instead of a read-modify-write of the whole array
    /// (<c>GetRtEntitiesByType</c> + <c>UpdateRecordArrayItem</c> + <c>AttributeUpdates</c> with the array) when
    /// several pipeline executions change items of the same entity in parallel: the read-modify-write loses
    /// concurrent changes, this does not. May be combined with <see cref="AttributeUpdates"/> for other attributes.
    /// </summary>
    [PropertyGroup("Data Mapping", 1)]
    public ICollection<RecordArrayItemUpdateConfiguration>? RecordArrayItemUpdates { get; set; }

    /// <summary>
    /// The path to the RtWellKnownName if available
    /// </summary>
    [PropertyGroup("Entity", 5, "jsonpath")]
    public string? RtWellKnownNamePath { get; set; }
}

/// <summary>
/// One atomic update of items of a RecordArray attribute, see
/// <see cref="CreateUpdateInfoNodeConfiguration.RecordArrayItemUpdates"/>.
/// </summary>
public record RecordArrayItemUpdateConfiguration
{
    /// <summary>
    /// Name of the RecordArray attribute of the entity (e.g. "States").
    /// </summary>
    public required string AttributeName { get; set; }

    /// <summary>
    /// Name of the record member that identifies the item (e.g. "ExternalId"): String, Integer, Integer64 or Enum.
    /// It cannot be written by the same update.
    /// </summary>
    public required string MatchAttributeName { get; set; }

    /// <summary>
    /// Static value to match against <see cref="MatchAttributeName"/>.
    /// </summary>
    public string? MatchValue { get; set; }

    /// <summary>
    /// JSONPath to the match value. Takes precedence over <see cref="MatchValue"/>. With <see cref="UpdatesPath"/>
    /// the path is relative to each element of the updates array.
    /// </summary>
    [PropertyGroup("", 0, "jsonpath")]
    public string? MatchValuePath { get; set; }

    /// <summary>
    /// Optional JSONPath to an array: one item update is created per element, <see cref="MatchValuePath"/> and the
    /// <c>ValuePath</c> of the attribute updates are relative to that element (like the updates array of
    /// UpdateRecordArrayItem@2). Without it the paths address the data document.
    /// </summary>
    [PropertyGroup("", 1, "jsonpath")]
    public string? UpdatesPath { get; set; }

    /// <summary>
    /// The members of the matched record to write: <c>Value</c> (static; <c>=now()</c> is the current UTC time) or
    /// <c>ValuePath</c>. Scalar members only (String, Integer, Integer64, Double, Boolean, DateTime,
    /// DateTimeOffset, TimeSpan, Enum); Secret members are refused by the engine. A path that yields no value
    /// writes null (an error for a mandatory member).
    /// </summary>
    public required ICollection<RecordAttributeUpdate> AttributeUpdates { get; set; }
}

using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Portable mapping export document shared by the ExportDataPointMappings and
/// ImportDataPointMappings nodes. Endpoints are identified by NATURAL keys
/// (identity attribute + value, name) so the document survives tenant
/// re-initialisation; RtIds are carried only as a same-tenant shortcut.
/// </summary>
internal sealed record DataPointMappingExportDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("mappingCkTypeId")] string MappingCkTypeId,
    [property: JsonPropertyName("mappings")] IReadOnlyList<ExportedMapping> Mappings);

/// <summary>One exported DataPointMapping with both endpoint references.</summary>
internal sealed record ExportedMapping(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("sourceAttributePath")] string SourceAttributePath,
    [property: JsonPropertyName("targetAttributePath")] string TargetAttributePath,
    [property: JsonPropertyName("mappingExpression")] string MappingExpression,
    [property: JsonPropertyName("source")] ExportedEntityRef? Source,
    [property: JsonPropertyName("target")] ExportedEntityRef? Target);

/// <summary>
/// Reference to one mapping endpoint. <c>rtId</c> is a hint valid only within
/// the exporting tenant; <c>identityAttribute</c>/<c>identityValue</c> (when
/// the exporter had an identity configured for the CK type) and <c>name</c>
/// are the portable keys.
/// </summary>
internal sealed record ExportedEntityRef(
    [property: JsonPropertyName("ckTypeId")] string CkTypeId,
    [property: JsonPropertyName("rtId")] string RtId,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name,
    [property: JsonPropertyName("identityAttribute")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IdentityAttribute,
    [property: JsonPropertyName("identityValue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IdentityValue);

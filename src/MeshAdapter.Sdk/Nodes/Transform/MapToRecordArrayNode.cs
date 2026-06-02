using System.Text.Json.Serialization;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Transforms a JSON object (key/value map) into an array of CK records with two
/// attributes per record — one for the key, one for the value.
/// Suitable for feeding a RecordArray attribute via CreateUpdateInfo@1.
/// </summary>
[NodeConfiguration(typeof(MapToRecordArrayNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class MapToRecordArrayNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<MapToRecordArrayNodeConfiguration>();

        var records = new List<RecordArrayItem>();

        if (dataContext.GetKind(config.Path) == DataKind.Object)
        {
            await dataContext.IterateObjectAsync(config.Path, (key, valueContext) =>
            {
                // Skip explicit-null values (legacy: kvp.Value == null continue).
                if (valueContext.GetKind("$") is DataKind.Null or DataKind.Undefined)
                {
                    return Task.CompletedTask;
                }

                // The value attribute is genuinely dynamic (any JSON kind). Reading it as
                // object? materializes scalars to their CLR form and objects/arrays to a
                // JsonElement, which re-serializes byte-identically to the source value.
                var value = valueContext.Get<object?>("$");

                // Attribute names are config-driven (dynamic) → a small object bag, not a
                // fixed record. The CkRecordId envelope stays a typed record.
                var attributes = new Dictionary<string, object?>
                {
                    [config.KeyAttributeName] = key,
                    [config.ValueAttributeName] = value
                };
                records.Add(new RecordArrayItem(new RecordTypeRef(config.CkRecordId), attributes));
                return Task.CompletedTask;
            });
        }

        dataContext.Set(config.TargetPath, records, config.DocumentMode, config.TargetValueKind,
            config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>CK RecordArray item: <c>{ "CkRecordId": {...}, "Attributes": {...} }</c>.</summary>
    internal sealed record RecordArrayItem(
        [property: JsonPropertyName("CkRecordId")] RecordTypeRef CkRecordId,
        [property: JsonPropertyName("Attributes")] IReadOnlyDictionary<string, object?> Attributes);

    internal sealed record RecordTypeRef(
        [property: JsonPropertyName("SemanticVersionedFullName")] string SemanticVersionedFullName);
}

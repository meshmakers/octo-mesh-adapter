using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Creates an update item for a RtEntity
/// </summary>
[NodeConfiguration(typeof(CreateUpdateInfoNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateUpdateInfoNode(NodeDelegate next, IMeshEtlContext etlContext, ICkCacheService ckCacheService)
    : IPipelineNode
{
    // Compact JSON with non-ASCII / HTML emitted literally, matching Newtonsoft's
    // ToString(Formatting.None). A String target attribute that receives an object/array is
    // stored as this JSON string; using the default encoder would escape non-ASCII (ü -> ü)
    // and diverge from pre-migration stored values, breaking downstream string equality
    // (e.g. CheckDuplicateNode's FieldFilter Equals). UnsafeRelaxedJsonEscaping still escapes
    // control chars / quotes / backslash, so it remains valid JSON.
    private static readonly JsonSerializerOptions CompactLiteralOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CreateUpdateInfoNodeConfiguration>();

        var rtId = GetRtId(dataContext, c);
        var updateKind = GetUpdateKind(dataContext, c);
        var rtWellKnownName = GetRtWellKnownName(dataContext, c);
        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        if (updateKind == null)
        {
            nodeContext.Error("update kind is not set. Please provide a UpdateKind or UpdateKindPath");
            return;
        }


        if (updateKind is UpdateKind.Update or UpdateKind.Delete && rtId == null)
        {
            nodeContext.Error("RtId is not set. Please provide a RtId or RtIdPath");
            return;
        }

        if ((c.AttributeUpdates == null || c.AttributeUpdates.Count == 0) && updateKind != UpdateKind.Delete) 
        {
            nodeContext.Error("AttributeUpdates is not set");
            return;
        }

        // we are most likely not the first node in a pipeline run. Otherwise, we just create a new list

        var timeStamp = DateTime.UtcNow;
        if (c.TimestampPath != null)
        {
            // Fall back to UtcNow when the configured path is absent/null. Get<DateTime?> yields
            // null for a missing path (whereas Get<DateTime> would silently produce
            // DateTime.MinValue) — keep the live timestamp instead of stamping the epoch minimum.
            var configured = dataContext.Get<DateTime?>(c.TimestampPath);
            if (configured.HasValue)
            {
                timeStamp = configured.Value;
            }
        }

        var rtEntity = new RtEntity
        {
            CkTypeId = ckTypeId,
            RtWellKnownName = rtWellKnownName
        };

        var hasUpdate = updateKind == UpdateKind.Delete; // if we are deleting, we have an update for sure
        foreach (var au in c.AttributeUpdates ?? [])
        {
            if (string.IsNullOrWhiteSpace(au.AttributeName))
            {
                nodeContext.Error("Attribute name is not set");
                continue;
            }

            if (au.AttributeValueType == null)
            {
                nodeContext.Error("Attribute value type is not set");
                continue;
            }

            if (!string.IsNullOrEmpty(au.ValuePath))
            {
                var path = au.ValuePath ?? "$";
                // Multi-match: wildcard / recursive-descent paths (e.g. "$.items[*].v") must
                // produce one update per match — restoring the legacy SelectTokens semantics
                // from the Newtonsoft implementation. SelectMatches yields a detached read
                // sub-context per match in document order; SetAttributeValue iterates them.
                var matches = dataContext.SelectMatches(path).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                // For String target attributes: serialize objects/arrays as JSON strings
                // so complex nested structures can be stored in a single String attribute.
                // Each match is serialized individually, mirroring the per-match update
                // behavior of the non-String branch.
                if (au.AttributeValueType == AttributeValueTypesDto.String &&
                    matches.All(m => m.GetKind("$") is DataKind.Object or DataKind.Array))
                {
                    foreach (var m in matches)
                    {
                        var jsonString = m.Get<JsonNode>("$")!.ToJsonString(CompactLiteralOptions);
                        if (SetAttributeValueSingle(nodeContext, au.AttributeName, jsonString, rtEntity))
                        {
                            hasUpdate = true;
                        }
                    }
                    continue;
                }

                hasUpdate |= SetAttributeValue(nodeContext, au.AttributeName, matches, rtEntity);
            }
            else if (au.Value != null)
            {
                if (!SetAttributeValueSingle(nodeContext, au.AttributeName, au.Value, rtEntity))
                {
                    return;
                }

                hasUpdate = true;
            }
        }

        if (hasUpdate)
        {
            rtEntity.RtChangedDateTime = timeStamp.ToUniversalTime();
            EntityUpdateInfo<RtEntity>? updateItem;
            if (updateKind == UpdateKind.Update)
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(new(ckTypeId, rtId!.Value), rtEntity);
            }
            else if (updateKind == UpdateKind.Delete)
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateDelete(new(ckTypeId, rtId!.Value));
            }
            else
            {
                if (rtId != null)
                {
                    rtEntity.RtId = rtId.Value;
                    rtEntity.CkTypeId = ckTypeId;
                    updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);
                }
                else
                {
                    updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(ckTypeId, rtEntity);
                }
            }

            dataContext.Set(c.TargetPath, updateItem, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }


        await next(dataContext, nodeContext);
    }

    private bool SetAttributeValue(INodeContext nodeContext, string attributeName,
        IEnumerable<IDataContext> matches, RtTypeWithAttributes rtTypeWithAttributes)
    {
        bool hasUpdate = false;
        foreach (var match in matches)
        {
            switch (match.GetKind("$"))
            {
                case DataKind.Object:
                {
                    // Objects may carry a CkRecordId → RtRecord; the structural conversion in
                    // GetAttributeValue needs the JsonObject, so read it via Get<JsonNode>.
                    var jObject = (JsonObject?)match.Get<JsonNode>("$");
                    if (jObject is not null &&
                        SetAttributeValueSingle(nodeContext, attributeName, jObject, rtTypeWithAttributes))
                    {
                        hasUpdate = true;
                    }

                    break;
                }
                case DataKind.Array:
                {
                    // Convert JsonArray items: primitives stay as primitives, objects get converted
                    // via GetAttributeValue (which handles CkRecordId → RtRecord conversion).
                    var jArray = (JsonArray?)match.Get<JsonNode>("$");
                    if (jArray is null) break;
                    var list = jArray.Select(item => item switch
                    {
                        JsonValue v => JsonScalar.ToClr(v),
                        _ => GetAttributeValue(nodeContext, item)
                    }).ToList();
                    if (SetAttributeValueSingle(nodeContext, attributeName, list, rtTypeWithAttributes))
                    {
                        hasUpdate = true;
                    }

                    break;
                }
                case DataKind.Null:
                {
                    if (SetAttributeValueSingle(nodeContext, attributeName, null, rtTypeWithAttributes))
                    {
                        hasUpdate = true;
                    }

                    break;
                }
                default:
                {
                    // Scalars (bool / long / double / DateTime / string) box via the shared
                    // JsonScalar rules exposed through GetValue — same parity as the former
                    // ExtractPrimitive ladder.
                    var raw = match.GetValue("$");
                    if (SetAttributeValueSingle(nodeContext, attributeName, raw, rtTypeWithAttributes))
                    {
                        hasUpdate = true;
                    }

                    break;
                }
            }
        }

        return hasUpdate;
    }

    private object? GetAttributeValue(INodeContext nodeContext, object? value)
    {
        // Minimal expression support (quick-fix, see issue #3917 for full framework).
        // Expressions start with '=' — currently supports: =now(), =utcNow(), =today(), =newGuid(), =epoch()
        if (value is string strValue && strValue.StartsWith('='))
        {
            var expr = strValue.Substring(1).Trim();
            return expr switch
            {
                "now" or "now()" or "utcNow" or "utcNow()" => DateTime.UtcNow,
                "today" or "today()" => DateTime.UtcNow.Date,
                "newGuid" or "newGuid()" => Guid.NewGuid().ToString(),
                "epoch" or "epoch()" => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "epochMs" or "epochMs()" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                _ => throw MeshAdapterPipelineExecutionException.InvalidValue(strValue)
            };
        }

        if (value is JsonObject jObject &&
            jObject.TryGetPropertyValue("CkRecordId", out var ckRecordIdNode) &&
            jObject.TryGetPropertyValue("Attributes", out var attributes) &&
            attributes is JsonObject attributesObject)
        {
            // CkRecordId is serialized as the SemanticVersionedFullName string by
            // RtCkIdRecordIdConverter. Accept the legacy reflection-emitted object shape too,
            // for inline JSON/YAML inputs that pass a structured CkRecordId.
            string? semanticName = ckRecordIdNode switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o => o[RtCkIdJsonShim.SemanticVersionedFullNameKey]?.GetValue<string>(),
                _ => null
            };
            if (semanticName == null)
            {
                throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, ckRecordIdNode);
            }

            var ckRecordGraphChild = ckCacheService.GetRtCkRecord(etlContext.TenantId, semanticName);
            var recordChild = new RtRecord
            {
                CkRecordId = ckRecordGraphChild.CkRecordId.ToRtCkId()
            };

            foreach (var kvp in attributesObject)
            {
                if (ckRecordGraphChild.AllAttributesByName.TryGetValue(kvp.Key, out var attribute))
                {
                    var childValue = GetAttributeValue(nodeContext, kvp.Value);
                    recordChild.SetAttributeValue(attribute.AttributeName, attribute.ValueType, childValue);
                }
                else
                {
                    throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, kvp.Value);
                }
            }

            return recordChild;
        }

        // For JsonValue, unwrap to native primitive via the shared JsonScalar boxing rules.
        if (value is JsonValue jv) return JsonScalar.ToClr(jv);

        return value;
    }

    private bool SetAttributeValueSingle(INodeContext nodeContext, string attributeName, object? value,
        RtTypeWithAttributes rtTypeWithAttributes)
    {
        var convertedValue = GetAttributeValue(nodeContext, value);

        RtPathEvaluator.SetValue(ckCacheService, etlContext.TenantId, rtTypeWithAttributes, attributeName,
            convertedValue);
        return true;
    }

    private static OctoObjectId? GetRtId(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.RtId != null)
        {
            return config.RtId.Value;
        }

        if (config.RtIdPath == null)
        {
            return null;
        }

        var rtId = dataContext.Get<OctoObjectId?>(config.RtIdPath);

        if (rtId == null && config.GenerateRtId)
        {
            rtId = OctoObjectId.GenerateNewId();
        }

        return rtId;
    }

    private static UpdateKind? GetUpdateKind(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.UpdateKind != null)
        {
            return config.UpdateKind;
        }

        if (config.UpdateKindPath == null)
        {
            return null;
        }

        return dataContext.Get<UpdateKind?>(config.UpdateKindPath);
    }

    private static string? GetRtWellKnownName(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.RtWellKnownNamePath == null)
        {
            return null;
        }

        return dataContext.Get<string?>(config.RtWellKnownNamePath);
    }
}
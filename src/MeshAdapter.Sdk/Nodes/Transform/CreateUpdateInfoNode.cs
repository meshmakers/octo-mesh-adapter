using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
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
                    if (jObject is null) break;

                    // Plain objects (no {CkRecordId, Attributes} envelope): coerce to the record
                    // type DECLARED by the CK schema when the target attribute is a
                    // Record/RecordArray. LLM/JSON payloads carry no CkRecordId discriminator;
                    // the schema already knows the record type (valueCkRecordId), so requiring
                    // the data to repeat it is redundant for non-polymorphic attributes.
                    // Envelope-shaped objects keep the existing GetAttributeValue path (and
                    // remain the ONLY way to address derived/polymorphic record types).
                    object valueToSet = jObject;
                    if (!IsRecordEnvelope(jObject))
                    {
                        var declaredRecordGraph =
                            TryResolveDeclaredRecordGraph(attributeName, rtTypeWithAttributes);
                        if (declaredRecordGraph is not null)
                        {
                            valueToSet = BuildRecordFromPlainObject(nodeContext, declaredRecordGraph, jObject);
                        }
                    }

                    if (SetAttributeValueSingle(nodeContext, attributeName, valueToSet, rtTypeWithAttributes))
                    {
                        hasUpdate = true;
                    }

                    break;
                }
                case DataKind.Array:
                {
                    // Convert JsonArray items: primitives stay as primitives, objects get converted
                    // via GetAttributeValue (which handles CkRecordId → RtRecord conversion).
                    // Plain objects without the record envelope coerce against the record type
                    // declared by the CK schema for this attribute (see DataKind.Object above);
                    // declaredRecordGraph is null when the target is not a schema-declared
                    // Record/RecordArray, in which case the pre-existing behavior applies.
                    var jArray = (JsonArray?)match.Get<JsonNode>("$");
                    if (jArray is null) break;
                    var declaredRecordGraph = TryResolveDeclaredRecordGraph(attributeName, rtTypeWithAttributes);
                    var list = jArray.Select(item => item switch
                    {
                        JsonValue v => JsonScalar.ToClr(v),
                        JsonObject plainObject when declaredRecordGraph is not null &&
                                                    !IsRecordEnvelope(plainObject) =>
                            BuildRecordFromPlainObject(nodeContext, declaredRecordGraph, plainObject),
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

    /// <summary>
    ///     True when the object carries the {CkRecordId, Attributes} record envelope handled by
    ///     <see cref="GetAttributeValue"/>.
    /// </summary>
    private static bool IsRecordEnvelope(JsonObject jObject)
    {
        return jObject.TryGetPropertyValue("CkRecordId", out _) &&
               jObject.TryGetPropertyValue("Attributes", out var attributes) &&
               attributes is JsonObject;
    }

    /// <summary>
    ///     Resolves the CK record graph DECLARED for a Record/RecordArray attribute of the target
    ///     entity type (the schema's valueCkRecordId). Returns null whenever the record type cannot
    ///     be inferred unambiguously — dotted attribute paths (nested targets), unknown attributes,
    ///     non-record value types, or a missing valueCkRecordId — in which case the caller keeps the
    ///     pre-existing behavior (plain objects require the {CkRecordId, Attributes} envelope).
    /// </summary>
    private CkRecordGraph? TryResolveDeclaredRecordGraph(string attributeName,
        RtTypeWithAttributes rtTypeWithAttributes)
    {
        // Dotted paths address attributes of nested records; the leaf schema is not resolved
        // here, so nested targets keep the envelope requirement.
        if (attributeName.Contains('.'))
        {
            return null;
        }

        if (rtTypeWithAttributes is not RtEntity { CkTypeId: { } entityCkTypeId } ||
            !ckCacheService.TryGetRtCkType(etlContext.TenantId, entityCkTypeId, out var ckTypeGraph))
        {
            return null;
        }

        // Exact match first, case-insensitive fallback second — mirrors how RtPathEvaluator
        // resolves configured attribute names against the CK graph.
        if (!ckTypeGraph!.AllAttributesByName.TryGetValue(attributeName, out var attributeGraph))
        {
            attributeGraph = ckTypeGraph.AllAttributesByName.Values.FirstOrDefault(a =>
                string.Equals(a.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase));
        }

        if (attributeGraph is null ||
            attributeGraph.ValueType is not (AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray) ||
            attributeGraph.ValueCkRecordId is null)
        {
            return null;
        }

        return ckCacheService.GetRtCkRecord(etlContext.TenantId, attributeGraph.ValueCkRecordId.ToRtCkId());
    }

    /// <summary>
    ///     Converts a plain JSON object (no {CkRecordId, Attributes} envelope) into an
    ///     <see cref="RtRecord"/> of the record type declared by the CK schema. Property names match
    ///     record attributes case-insensitively (LLM/JSON payloads are typically camelCase while CK
    ///     attributes are PascalCase); unknown properties throw — the same strictness as the envelope
    ///     path, so typos fail loudly instead of silently dropping data. Nested Record/RecordArray
    ///     attributes recurse with their own declared record type.
    /// </summary>
    private RtRecord BuildRecordFromPlainObject(INodeContext nodeContext, CkRecordGraph recordGraph,
        JsonObject plainObject)
    {
        if (recordGraph.IsAbstract)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                (object)($"Record type '{recordGraph.CkRecordId}' is abstract; a plain JSON object cannot be " +
                         "coerced to it. Provide the {CkRecordId, Attributes} envelope with a concrete record type."));
        }

        var record = new RtRecord
        {
            CkRecordId = recordGraph.CkRecordId.ToRtCkId()
        };

        foreach (var kvp in plainObject)
        {
            if (!recordGraph.AllAttributesByName.TryGetValue(kvp.Key, out var attribute))
            {
                attribute = recordGraph.AllAttributesByName.Values.FirstOrDefault(a =>
                    string.Equals(a.AttributeName, kvp.Key, StringComparison.OrdinalIgnoreCase));
            }

            if (attribute is null)
            {
                throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                    (object)($"Property '{kvp.Key}' does not match any attribute of record type " +
                             $"'{recordGraph.CkRecordId}'"));
            }

            var childValue = ConvertRecordChildValue(nodeContext, attribute, kvp.Value);
            record.SetAttributeValue(attribute.AttributeName, attribute.ValueType, childValue);
        }

        return record;
    }

    /// <summary>
    ///     Converts a single property value while building a record from a plain JSON object.
    ///     Nested plain records/record-arrays coerce against the child attribute's declared record
    ///     type; everything else (scalars, envelope-shaped objects) takes the
    ///     <see cref="GetAttributeValue"/> path.
    /// </summary>
    private object? ConvertRecordChildValue(INodeContext nodeContext, CkTypeAttributeGraph attribute,
        JsonNode? value)
    {
        if (attribute is { ValueType: AttributeValueTypesDto.Record, ValueCkRecordId: not null } &&
            value is JsonObject plainChild && !IsRecordEnvelope(plainChild))
        {
            var childGraph = ckCacheService.GetRtCkRecord(etlContext.TenantId,
                attribute.ValueCkRecordId.ToRtCkId());
            return BuildRecordFromPlainObject(nodeContext, childGraph, plainChild);
        }

        if (attribute is { ValueType: AttributeValueTypesDto.RecordArray, ValueCkRecordId: not null } &&
            value is JsonArray childArray)
        {
            var childGraph = ckCacheService.GetRtCkRecord(etlContext.TenantId,
                attribute.ValueCkRecordId.ToRtCkId());
            return childArray.Select(item =>
                item is JsonObject plainItem && !IsRecordEnvelope(plainItem)
                    ? BuildRecordFromPlainObject(nodeContext, childGraph, plainItem)
                    : GetAttributeValue(nodeContext, item)).ToList();
        }

        return GetAttributeValue(nodeContext, value);
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
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

        var hasItemUpdateConfiguration = c.RecordArrayItemUpdates is { Count: > 0 };
        if ((c.AttributeUpdates == null || c.AttributeUpdates.Count == 0) && !hasItemUpdateConfiguration &&
            updateKind != UpdateKind.Delete)
        {
            nodeContext.Error("AttributeUpdates is not set");
            return;
        }

        if (hasItemUpdateConfiguration && updateKind != UpdateKind.Update)
        {
            nodeContext.Error("RecordArrayItemUpdates are only supported with UpdateKind UPDATE");
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

        // AB#7442: atomic updates of single RecordArray items, executed by the engine in one statement.
        var itemUpdates = hasItemUpdateConfiguration
            ? await BuildRecordArrayItemUpdatesAsync(dataContext, nodeContext, c.RecordArrayItemUpdates!)
            : [];
        if (itemUpdates.Count > 0)
        {
            hasUpdate = true;
        }

        if (hasUpdate)
        {
            rtEntity.RtChangedDateTime = timeStamp.ToUniversalTime();
            EntityUpdateInfo<RtEntity>? updateItem;
            if (updateKind == UpdateKind.Update && itemUpdates.Count > 0)
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(new(ckTypeId, rtId!.Value),
                    rtEntity, itemUpdates);
            }
            else if (updateKind == UpdateKind.Update)
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

    /// <summary>
    /// AB#7442: turns the configured <see cref="RecordArrayItemUpdateConfiguration" /> entries into engine item
    /// updates. An entry whose match value is empty is skipped with a warning (as UpdateRecordArrayItem skips a
    /// missing record); with <c>UpdatesPath</c> one item update is created per element of the array.
    /// </summary>
    private static async Task<List<RtRecordArrayItemUpdate>> BuildRecordArrayItemUpdatesAsync(
        IDataContext dataContext, INodeContext nodeContext,
        IEnumerable<RecordArrayItemUpdateConfiguration> configurations)
    {
        var result = new List<RtRecordArrayItemUpdate>();
        foreach (var configuration in configurations)
        {
            if (string.IsNullOrWhiteSpace(configuration.AttributeName) ||
                string.IsNullOrWhiteSpace(configuration.MatchAttributeName))
            {
                nodeContext.Error("RecordArrayItemUpdates: AttributeName and MatchAttributeName must be set");
                continue;
            }

            if (configuration.AttributeUpdates == null || configuration.AttributeUpdates.Count == 0)
            {
                nodeContext.Error("RecordArrayItemUpdates: AttributeUpdates of '{0}' is not set",
                    configuration.AttributeName);
                continue;
            }

            if (string.IsNullOrWhiteSpace(configuration.UpdatesPath))
            {
                AddRecordArrayItemUpdate(dataContext, nodeContext, configuration, result);
                continue;
            }

            if (dataContext.GetKind(configuration.UpdatesPath) != DataKind.Array)
            {
                nodeContext.Warning("RecordArrayItemUpdates: no array of updates found at path '{0}'",
                    configuration.UpdatesPath);
                continue;
            }

            await dataContext.IterateArrayAsync(configuration.UpdatesPath, itemContext =>
            {
                AddRecordArrayItemUpdate(itemContext, nodeContext, configuration, result);
                return Task.CompletedTask;
            });
        }

        return result;
    }

    private static void AddRecordArrayItemUpdate(IDataContext context, INodeContext nodeContext,
        RecordArrayItemUpdateConfiguration configuration, List<RtRecordArrayItemUpdate> result)
    {
        // As in UpdateRecordArrayItem: the key is compared as text; the engine converts it to the type of the key
        // member (String, Integer, Integer64, Enum).
        var matchValue = configuration.MatchValuePath != null
            ? context.Get<JsonNode>(configuration.MatchValuePath)?.ToString()
            : configuration.MatchValue;
        if (string.IsNullOrEmpty(matchValue))
        {
            nodeContext.Warning("RecordArrayItemUpdates: match value of '{0}' is null or empty, update skipped",
                configuration.AttributeName);
            return;
        }

        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var update in configuration.AttributeUpdates)
        {
            if (string.IsNullOrWhiteSpace(update.AttributeName))
            {
                nodeContext.Error("RecordArrayItemUpdates: attribute name of an update is not set");
                continue;
            }

            members[update.AttributeName] = ResolveMemberValue(update, context);
        }

        if (members.Count > 0)
        {
            result.Add(new RtRecordArrayItemUpdate(configuration.AttributeName, configuration.MatchAttributeName,
                matchValue, members));
        }
    }

    private static object? ResolveMemberValue(RecordAttributeUpdate update, IDataContext context)
    {
        if (update.ValuePath != null)
        {
            return ReadScalar(context.Get<JsonNode>(update.ValuePath));
        }

        if (update.Value is string text && text == "=now()")
        {
            // ISO-8601 text like UpdateRecordArrayItem: it fits a String member as well as a DateTime member
            // (the engine parses it).
            return DateTime.UtcNow.ToString("O");
        }

        return update.Value is null
            ? null
            : ReadScalar(JsonSerializer.SerializeToNode(update.Value, SystemTextJsonOptions.Default));
    }

    /// <summary>A JSON scalar as CLR value; objects and arrays are handed on as they are (the engine refuses them).</summary>
    private static object? ReadScalar(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue value => JsonScalar.ToClr(value),
            _ => node.ToJsonString()
        };
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

        // AB#5538: a Secret read earlier arrives as the marker {"isSet": …}. Copied into an update it
        // means "unchanged" - the engine's own reading of a marker - and must never become a value.
        if (value is JsonObject marker && PipelineSecretValues.IsSecretMarker(marker))
        {
            return RtSecretValue.Pending(string.Empty);
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
                    if (attribute.ValueType == AttributeValueTypesDto.Secret && childValue is string plaintext)
                    {
                        // AB#5538: see PreserveSecretPlaintext - the plaintext must cross the data
                        // context as a string, a pending RtSecretValue would arrive as "unchanged".
                        recordChild.SetAttributeRawValue(attribute.AttributeName, plaintext);
                        nodeContext.RegisterSecret(plaintext);
                    }
                    else
                    {
                        recordChild.SetAttributeValue(attribute.AttributeName, attribute.ValueType, childValue);
                    }
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
        if (convertedValue is string plaintext)
        {
            PreserveSecretPlaintext(nodeContext, attributeName, plaintext, rtTypeWithAttributes);
        }

        return true;
    }

    /// <summary>
    /// AB#5538: writing a string into a <c>Secret</c> attribute is how a pipeline sets a secret (the
    /// engine encrypts it on <c>ApplyChanges@2</c>; e.g. a refreshed OAuth token written back). The
    /// path evaluator converts the string into a pending <see cref="RtSecretValue" />, but the update
    /// travels to <c>ApplyChanges@2</c> through the data context, and every serialiser writes an
    /// <see cref="RtSecretValue" /> only as the marker <c>{"isSet": …}</c> — which reads back as
    /// "unchanged". The value would be silently dropped. So the plaintext is put back as a plain string
    /// (the engine treats a string in a Secret slot as new input) and registered as secret, so debug
    /// snapshots of the update show <c>***</c>. An empty string keeps meaning "unchanged".
    /// Covers top-level attributes and dotted paths through single records; records given as JSON
    /// objects are handled where they are built (<see cref="GetAttributeValue" />).
    /// </summary>
    private void PreserveSecretPlaintext(INodeContext nodeContext, string attributePath, string plaintext,
        RtTypeWithAttributes root)
    {
        var target = root;
        var name = attributePath;
        var lastDot = attributePath.LastIndexOf('.');
        if (lastDot > 0)
        {
            if (attributePath.IndexOfAny(['[', ']', '-', ':', '>']) >= 0)
            {
                return;
            }

            if (RtPathEvaluator.GetValue(ckCacheService, etlContext.TenantId, root,
                    attributePath[..lastDot]) is not RtTypeWithAttributes parent)
            {
                return;
            }

            target = parent;
            name = attributePath[(lastDot + 1)..];
        }

        var key = target.Attributes.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key != null && target.Attributes[key] is RtSecretValue { IsPending: true })
        {
            target.SetAttributeRawValue(key, plaintext);
            nodeContext.RegisterSecret(plaintext);
        }
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
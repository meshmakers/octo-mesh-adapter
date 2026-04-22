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
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Creates an update item for a RtEntity
/// </summary>
[NodeConfiguration(typeof(CreateUpdateInfoNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateUpdateInfoNode(NodeDelegate next, IMeshEtlContext etlContext, ICkCacheService ckCacheService)
    : IPipelineNode
{
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
        if (c.TimestampPath != null && dataContext.Current != null)
        {
            timeStamp = dataContext.GetSimpleValueByPath<DateTime>(c.TimestampPath);
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
                var jTokens = dataContext.Current?.SelectTokens(au.ValuePath ?? "$") ??
                              dataContext.Current?[au.ValuePath ?? "$"];

                if (jTokens == null)
                {
                    continue;
                }

                // For String target attributes: serialize JObject/JArray values as JSON strings
                // so complex nested structures can be stored in a single String attribute.
                if (au.AttributeValueType == AttributeValueTypesDto.String)
                {
                    var materialized = jTokens.ToList();
                    if (materialized.Count == 1 && materialized[0] is JContainer container)
                    {
                        var jsonString = container.ToString(Newtonsoft.Json.Formatting.None);
                        if (SetAttributeValueSingle(nodeContext, au.AttributeName, jsonString, rtEntity))
                        {
                            hasUpdate = true;
                        }
                        continue;
                    }

                    jTokens = materialized;
                }

                hasUpdate |= SetAttributeValue(nodeContext, au.AttributeName, jTokens, rtEntity);
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

            dataContext.SetValueByPath(c.TargetPath, updateItem, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }


        await next(dataContext, nodeContext);
    }

    private bool SetAttributeValue(INodeContext nodeContext, string attributeName, IEnumerable<JToken> jTokens,
        RtTypeWithAttributes rtTypeWithAttributes)
    {
        bool hasUpdate = false;
        foreach (var jToken in jTokens)
        {
            if (jToken is JValue jValue)
            {
                if (!SetAttributeValueSingle(nodeContext, attributeName, jValue.Value,
                        rtTypeWithAttributes))
                {
                    continue;
                }

                hasUpdate = true;
            }
            else if (jToken is JObject jObject)
            {
                if (!SetAttributeValueSingle(nodeContext, attributeName, jObject,
                        rtTypeWithAttributes))
                {
                    continue;
                }

                hasUpdate = true;
            }
            else if (jToken is JArray jArray)
            {
                // Convert JArray items: JValues stay as primitives, JObjects get converted
                // via GetAttributeValue (which handles CkRecordId → RtRecord conversion).
                var list = jArray.Select(item => item switch
                {
                    JValue v => v.Value,
                    _ => GetAttributeValue(nodeContext, item)
                }).ToList();
                if (!SetAttributeValueSingle(nodeContext, attributeName, list, rtTypeWithAttributes))
                {
                    continue;
                }

                hasUpdate = true;
            }
            else
            {
                nodeContext.Error($"Value {jToken} is not a valid type");
                throw MeshAdapterPipelineExecutionException.InvalidValue(jToken);
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

        if (value is JObject jObject && jObject.TryGetValue("CkRecordId", out var ckRecordIdToken) &&
            ckRecordIdToken is JObject ckRecordIdObject)
        {
            if (jObject.TryGetValue("Attributes", out var attributes) && attributes is JObject attributesObject)
            {
                var ckRecordGraphChild = ckCacheService.GetRtCkRecord(etlContext.TenantId,
                    ckRecordIdObject["SemanticVersionedFullName"]!.ToObject<string>()!);
                var recordChild = new RtRecord
                {
                    CkRecordId = ckRecordGraphChild.CkRecordId.ToRtCkId()
                };

                foreach (var jToken in attributesObject.Properties())
                {
                    if (ckRecordGraphChild.AllAttributesByName.TryGetValue(jToken.Name, out var attribute))
                    {
                        var childValue = GetAttributeValue(nodeContext, jToken.Value);
                        recordChild.SetAttributeValue(attribute.AttributeName, attribute.ValueType, childValue);
                    }
                    else
                    {
                        throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, jToken);
                    }
                }

                return recordChild;
            }
        }

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

        if (config.RtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var rtId = dataContext.GetComplexObjectByPath<OctoObjectId?>(config.RtIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);

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

        if (config.UpdateKindPath == null || dataContext.Current == null)
        {
            return null;
        }

        var updateKind =
            dataContext.GetComplexObjectByPath<UpdateKind?>(config.UpdateKindPath,
                RtNewtonsoftSerializer.DefaultSerializer);
        return updateKind;
    }

    private static string? GetRtWellKnownName(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.RtWellKnownNamePath == null || dataContext.Current == null)
        {
            return null;
        }

        var rtWellKnownName =
            dataContext.GetComplexObjectByPath<string?>(config.RtWellKnownNamePath,
                RtNewtonsoftSerializer.DefaultSerializer);
        return rtWellKnownName;
    }
}
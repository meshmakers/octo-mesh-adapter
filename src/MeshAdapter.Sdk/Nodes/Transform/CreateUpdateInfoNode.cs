using Meshmakers.Octo.ConstructionKit.Contracts;
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
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Creates an update item for an existing RtEntity
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

        if (c.CkTypeId == null)
        {
            nodeContext.Error("CkTypeId is not set");
            return;
        }

        if (updateKind == null)
        {
            nodeContext.Error("update kind is not set. Please provide a UpdateKind or UpdateKindPath");
            return;
        }


        if ((updateKind is UpdateKind.Update or UpdateKind.Delete) && rtId == null)
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

        var ckTypeGraph = await etlContext.TenantRepository.GetCkTypeGraphAsync(c.CkTypeId);

        var rtEntity = new RtEntity
        {
            CkTypeId = c.CkTypeId,
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

                hasUpdate |= SetAttributeValue(nodeContext, au.AttributeName, jTokens, rtEntity, ckTypeGraph);
            }
            else if (au.Value != null)
            {
                if (!SetAttributeValueSingle(nodeContext, au.AttributeName, au.Value, rtEntity, ckTypeGraph))
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
                updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(new(c.CkTypeId, rtId!.Value), rtEntity);
            }
            else if (updateKind == UpdateKind.Delete)
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateDelete(new(c.CkTypeId, rtId!.Value));
            }
            else
            {
                if (rtId != null)
                {
                    rtEntity.RtId = rtId.Value;
                    rtEntity.CkTypeId = c.CkTypeId;
                    updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);
                }
                else
                {
                    updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(c.CkTypeId, rtEntity);
                }
            }

            dataContext.SetValueByPath(c.TargetPath, updateItem, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }


        await next(dataContext, nodeContext);
    }

    private bool SetAttributeValue(INodeContext nodeContext, string attributeName, IEnumerable<JToken> jTokens,
        RtTypeWithAttributes rtTypeWithAttributes, CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
    {
        bool hasUpdate = false;
        foreach (var jToken in jTokens)
        {
            if (jToken is JValue jValue)
            {
                if (!SetAttributeValueSingle(nodeContext, attributeName, jValue.Value,
                        rtTypeWithAttributes, ckTypeWithAttributesGraph))
                {
                    continue;
                }

                hasUpdate = true;
            }
            else if (jToken is JObject jObject)
            {
                if (!SetAttributeValueSingle(nodeContext, attributeName, jObject,
                        rtTypeWithAttributes, ckTypeWithAttributesGraph))
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
        if (value is JObject jObject && jObject.TryGetValue("CkRecordId", out var ckRecordIdToken) &&
            ckRecordIdToken is JObject ckRecordIdObject)
        {
            if (jObject.TryGetValue("Attributes", out var attributes) && attributes is JObject attributesObject)
            {
                var ckRecordGraphChild = ckCacheService.GetCkRecord(etlContext.TenantId,
                    ckRecordIdObject["FullName"]!.ToObject<string>()!);
                var recordChild = new RtRecord
                {
                    CkRecordId = ckRecordGraphChild.CkRecordId
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
        RtTypeWithAttributes rtTypeWithAttributes, CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
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
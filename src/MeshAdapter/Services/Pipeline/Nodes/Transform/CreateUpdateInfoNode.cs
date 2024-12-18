using Meshmakers.Common.Shared;
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
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;

/// <summary>
/// Creates an update item for an existing RtEntity
/// </summary>
[NodeConfiguration(typeof(CreateUpdateInfoNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateUpdateInfoNode(NodeDelegate next, IMeshEtlContext etlContext, ICkCacheService ckCacheService)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<CreateUpdateInfoNodeConfiguration>();

        var rtId = GetRtId(dataContext, c);
        var updateKind = GetUpdateKind(dataContext, c);
        var rtWellKnownName = GetRtWellKnownName(dataContext, c);

        if (c.CkTypeId == null)
        {
            dataContext.NodeContext.Error("CkTypeId is not set");
            return;
        }

        if (updateKind == null)
        {
            dataContext.NodeContext.Error("update kind is not set. Please provide a UpdateKind or UpdateKindPath");
            return;
        }
        
        
        if (updateKind == UpdateKind.Update && rtId == null)
        {
            dataContext.NodeContext.Error("RtId is not set. Please provide a RtId or RtIdPath");
            return;
        }

        if (c.AttributeUpdates == null || c.AttributeUpdates.Count == 0)
        {
            dataContext.NodeContext.Error("AttributeUpdates is not set");
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
            RtWellKnownName = rtWellKnownName
        };

        var hasUpdate = false;
        foreach (var au in c.AttributeUpdates)
        {
            if (string.IsNullOrWhiteSpace(au.AttributeName))
            {
                dataContext.NodeContext.Error("Attribute name is not set");
                continue;
            }

            if (au.AttributeValueType == null)
            {
                dataContext.NodeContext.Error("Attribute value type is not set");
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
                
                hasUpdate |= SetAttributeValue(dataContext, au.AttributeName, jTokens, rtEntity, ckTypeGraph);
            }
            else if (au.Value != null)
            {
                if (!SetAttributeValueSingle(dataContext, au.AttributeName, au.Value, rtEntity, ckTypeGraph))
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
                updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId!.Value), rtEntity);
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

            dataContext.SetValueByPath(c.TargetPath, updateItem, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }


        await next(dataContext);
    }

    private bool SetAttributeValue(IDataContext dataContext, string attributeName, IEnumerable<JToken> jTokens, RtTypeWithAttributes rtTypeWithAttributes, CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
    {
        bool hasUpdate = false;
        foreach (var jToken in jTokens)
        {
            if (jToken is JValue jValue)
            {
                if (!SetAttributeValueSingle(dataContext, attributeName, jValue.Value,
                        rtTypeWithAttributes, ckTypeWithAttributesGraph))
                {
                    continue;
                }
                        
                hasUpdate = true;
            }
            else if (jToken is JObject jObject)
            {
                if (!SetAttributeValueSingle(dataContext, attributeName, jObject,
                        rtTypeWithAttributes, ckTypeWithAttributesGraph))
                {
                    continue;
                }
                        
                hasUpdate = true;
            }
            else
            {
                dataContext.NodeContext.Error($"Value {jToken} is not a valid type");
                throw MeshAdapterPipelineExecutionException.InvalidValue(jToken);
            }
        }

        return hasUpdate;
    }

    private bool SetAttributeValueSingle(IDataContext dataContext, string attributeName, object? value, RtTypeWithAttributes rtTypeWithAttributes, CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
    {
        if (!ckTypeWithAttributesGraph.AllAttributesByName.TryGetValue(attributeName, out var attribute))
        {
            dataContext.NodeContext.Error($"Attribute {attributeName} not found in construction kit type {ckTypeWithAttributesGraph}");
            return false;
        }
        
        switch (attribute.ValueType)
        {
            case AttributeValueTypesDto.Enum:

                if (attribute.ValueCkEnumId != null)
                {
                    var ckEnumGraph = ckCacheService.GetCkEnum(etlContext.TenantId, attribute.ValueCkEnumId);

                    if (value is string strValue)
                    {
                        var enumValue = ckEnumGraph.Values.FirstOrDefault(v =>
                            string.Compare(v.Name, strValue, StringComparison.OrdinalIgnoreCase) == 0);
                        if (enumValue == null)
                        {
                            dataContext.NodeContext.Error(
                                $"Enum value {strValue} not found in CKEnum {attribute.ValueCkEnumId}");
                            return false;
                        }

                        rtTypeWithAttributes.SetAttributeValue(attributeName, attribute.ValueType, enumValue.Key);
                        return true;
                    }

                    if (value is int intValue)
                    {
                        var enumValue = ckEnumGraph.Values.FirstOrDefault(v => v.Key == intValue);
                        if (enumValue == null)
                        {
                            dataContext.NodeContext.Error(
                                $"Enum value {intValue} not found in CKEnum {attribute.ValueCkEnumId}");
                            return false;
                        }

                        rtTypeWithAttributes.SetAttributeValue(attributeName, attribute.ValueType, enumValue.Key);
                        return true;
                    }

                    dataContext.NodeContext.Error($"Enum value {value} is not a valid type");
                    return false;
                }
                dataContext.NodeContext.Error("Enum value is not set");
                return false;
            case AttributeValueTypesDto.Record:
                if (attribute.ValueCkRecordId != null)
                {
                    var ckRecordGraph = ckCacheService.GetCkRecord(etlContext.TenantId, attribute.ValueCkRecordId);

                    if (value is JObject jsObject)
                    {
                        bool hasUpdate = false;
                        var record = new RtRecord
                        {
                            CkRecordId = attribute.ValueCkRecordId
                        };
                        
                        foreach (var ckTypeAttributeGraphKeyValue in ckRecordGraph.AllAttributesByName)
                        {
                            if (!jsObject.TryGetValue(ckTypeAttributeGraphKeyValue.Key.ToPascalCase(), out var jToken) && 
                                !jsObject.TryGetValue(ckTypeAttributeGraphKeyValue.Key.ToCamelCase(), out jToken))
                            {
                                continue;
                            }
                            
                            hasUpdate |= SetAttributeValueSingle(dataContext, ckTypeAttributeGraphKeyValue.Key, jToken,
                                record, ckRecordGraph);
                        }
                        
                        rtTypeWithAttributes.SetAttributeValue(attributeName, attribute.ValueType, record);

                        return hasUpdate;
                    }

                    dataContext.NodeContext.Error($"Enum value {value} is not a valid type");
                    return false;
                }
                dataContext.NodeContext.Error("Record value is not set");
                return false;
            default:
                rtTypeWithAttributes.SetAttributeValue(attributeName, attribute.ValueType, value);
                return true;
        }
    }
    
    private static OctoObjectId? GetRtId(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.RtId != null)
        {
            return config.RtId.Value;
        }
        if(config.RtIdPath == null || dataContext.Current == null)
        {
            return null;
        }
        
        var rtId = dataContext.GetComplexObjectByPath<OctoObjectId?>(config.RtIdPath, RtNewtonsoftSerializer.DefaultSerializer);

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
        
        var updateKind = dataContext.GetComplexObjectByPath<UpdateKind?>(config.UpdateKindPath, RtNewtonsoftSerializer.DefaultSerializer);
        return updateKind;
    }
    
    private static string? GetRtWellKnownName(IDataContext dataContext, CreateUpdateInfoNodeConfiguration config)
    {
        if (config.RtWellKnownNamePath == null || dataContext.Current == null)
        {
            return null;
        }
        
        var rtWellKnownName = dataContext.GetComplexObjectByPath<string?>(config.RtWellKnownNamePath, RtNewtonsoftSerializer.DefaultSerializer);
        return rtWellKnownName;
    }
}
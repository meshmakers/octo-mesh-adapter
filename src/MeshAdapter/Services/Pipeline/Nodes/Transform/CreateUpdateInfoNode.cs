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

        if (c.CkTypeId == null)
        {
            dataContext.NodeContext.Error("CkTypeId is not set");
            return;
        }

        if (c is { UpdateKind: UpdateKind.Update, RtId: null, RtIdPath: null })
        {
            dataContext.NodeContext.Error("RtId and RtIdPath is not set");
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

        var rtEntity = new RtEntity();
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

                foreach (var jToken in jTokens)
                {
                    if (jToken is JValue jValue)
                    {
                        if (!SetAttributeValue(dataContext, au.AttributeName, au.AttributeValueType.Value, jValue.Value,
                                rtEntity, ckTypeGraph))
                        {
                            continue;
                        }
                        
                        hasUpdate = true;
                    }
                }
            }
            else if (au.Value != null)
            {
                if (!SetAttributeValue(dataContext, au.AttributeName, au.AttributeValueType.Value, au.Value, rtEntity, ckTypeGraph))
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
            if (c.UpdateKind == UpdateKind.Update)
            {
                var rtId = c.RtId ?? dataContext.GetSimpleValueByPath<OctoObjectId?>(c.RtIdPath ?? "$");
                if (rtId == null)
                {
                    dataContext.NodeContext.Error("RtId or RtIdPath is not set");
                    return;
                }

                updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId.Value), rtEntity);
            }
            else
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(c.CkTypeId, rtEntity);
            }

            dataContext.SetValueByPath(c.TargetPath, updateItem, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }


        await next(dataContext);
    }

    private bool SetAttributeValue(IDataContext dataContext, string attributeName, AttributeValueTypesDto attributeValueType, object? value, RtEntity rtEntity, CkTypeGraph ckTypeGraph)
    {
        if (!ckTypeGraph.AllAttributesByName.TryGetValue(attributeName, out var attribute))
        {
            dataContext.NodeContext.Error($"Attribute {attributeName} not found in CKType {ckTypeGraph.CkTypeId}");
            return false;
        }
        
        switch (attributeValueType)
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

                        rtEntity.SetAttributeValue(attributeName, attributeValueType, enumValue.Key);
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

                        rtEntity.SetAttributeValue(attributeName, attributeValueType, enumValue.Key);
                        return true;
                    }

                    dataContext.NodeContext.Error($"Enum value {value} is not a valid type");
                    return false;
                }
                dataContext.NodeContext.Error("Enum value is not set");
                return false;
            default:
                rtEntity.SetAttributeValue(attributeName, attributeValueType, value);
                return true;
        }
    }
}
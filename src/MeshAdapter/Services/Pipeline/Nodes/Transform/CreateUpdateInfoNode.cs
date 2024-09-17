using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Transform;
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
public class CreateUpdateInfoNode(NodeDelegate next) : IPipelineNode
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
                        object? value;
                        switch (au.AttributeValueType.Value)
                        {
                            case AttributeValueTypesDto.Double:
                                value = jValue.Value<double>();
                                break;
                            case AttributeValueTypesDto.Int:
                                value = jValue.Value<int>();
                                break;
                            case AttributeValueTypesDto.Boolean:
                                value = jValue.Value<bool>();
                                break;
                            case AttributeValueTypesDto.String:
                                value = jValue.Value<string>();
                                break;
                            case AttributeValueTypesDto.DateTime:
                                value = jValue.Value<DateTime>();
                                break;
                            case AttributeValueTypesDto.Int64:
                                value = jValue.Value<long>();
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        rtEntity.SetAttributeValue(au.AttributeName, au.AttributeValueType.Value, value);
                        hasUpdate = true;
                    }
                }
            }
            else if (au.Value != null)
            {
                rtEntity.SetAttributeValue(au.AttributeName, au.AttributeValueType.Value, au.Value);
                hasUpdate = true;
            }
        }

        EntityUpdateInfo<RtEntity>? updateItem = null;
        if (hasUpdate)
        {
            rtEntity.RtChangedDateTime = timeStamp.ToUniversalTime();
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
        }

        dataContext.SetValueByPath(c.TargetPath, updateItem, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);
    }
}
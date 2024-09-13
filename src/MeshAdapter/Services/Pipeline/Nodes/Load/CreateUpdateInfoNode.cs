using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

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
        var c = dataContext.GetNodeConfiguration<CreateUpdateInfoNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "CkTypeId is not set");
            return;
        }

        if (c.UpdateKind == UpdateKind.Update && c.RtId == null && c.RtIdPath == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtId and RtIdPath is not set");
            return;
        }

        if (c.AttributeUpdates == null || c.AttributeUpdates.Count == 0)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "AttributeUpdates is not set");
            return;
        }

        if (dataContext.Current == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Current is not set");
            return;
        }

        if (c.TargetPath == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "TargetPath is not set");
            return;
        }

        // we are most likely not the first node in a pipeline run. Otherwise, we just create a new list

        var timeStamp = DateTime.UtcNow;
        if (c.TimestampPath != null)
        {
            timeStamp = dataContext.GetCurrentValueByPath<DateTime>(c.TimestampPath);
        }

        var rtEntity = new RtEntity();
        var hasUpdate = false;
        foreach (var au in c.AttributeUpdates)
        {
            if (string.IsNullOrWhiteSpace(au.AttributeName))
            {
                dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Attribute name is not set");
                continue;
            }

            if (au.AttributeValueType == null)
            {
                dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Attribute value type is not set");
                continue;
            }

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

        EntityUpdateInfo<RtEntity>? updateItem = null;
        if (hasUpdate)
        {
            rtEntity.RtChangedDateTime = timeStamp;
            if (c.UpdateKind == UpdateKind.Update)
            {
                var rtId = c.RtId ?? dataContext.GetCurrentValueByPath<OctoObjectId?>(c.RtIdPath ?? "$");
                if (rtId == null)
                {
                    dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtId or RtIdPath is not set");
                    return;
                }

                updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId.Value), rtEntity);
            }
            else
            {
                updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(c.CkTypeId, rtEntity);
            }
        }

        dataContext.SetCurrentValueByPath(c.TargetPath, updateItem, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);
    }
}
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Creates an update item for an existing RtEntity
/// </summary>
[NodeConfiguration(typeof(CreateUpdateInfoNodeConfiguration))]
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

        if (c.RtId == null && c.RtIdPath == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtId and RtIdPath is not set");
            return;
        }

        if (c.AttributeUpdates == null || c.AttributeUpdates.Count == 0)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "AttributeUpdates is not set");
            return;
        }

        var rtId = c.RtId ?? dataContext.Current?.SelectToken(c.RtIdPath ?? "$")?.ToObject<OctoObjectId>();

        if (rtId == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtId is not set");
            return;
        }

        if (dataContext.Current == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Current is not set");
            return;
        }

        if (c.TargetPropertyName == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "TargetPropertyName is not set");
            return;
        }

        if (NoUpdatesForCurrentNode(dataContext, c))
        {
            // let other nodes try their luck
            await next(dataContext);
            return;
        }

        // we are most likley not the first node in a pipeline run. Otherwise we just create a new list
        var updateList = dataContext.Current?.SelectToken(c.TargetPropertyName)
            ?.ToObject<List<EntityUpdateInfo<RtEntity>>>() ?? [];


        var rtEntity = new RtEntity();

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

            var jToken = dataContext.Current?.SelectToken(au.ValuePath ?? "$") ??
                         dataContext.Current?[au.ValuePath ?? "$"];
            object? value = null;
            if (jToken != null && jToken is JValue jValue)
            {
                switch (au.AttributeValueType.Value)
                {
                    case AttributeValueTypesDto.Double:
                        value = jValue.Value<double>();
                        break;
                    case AttributeValueTypesDto.Int:
                        value = jValue.Value<int>();
                        break;
                }

                rtEntity.SetAttributeValue(au.AttributeName, au.AttributeValueType.Value, value);
                updateList.Add(
                    EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId.Value), rtEntity));
            }
        }

        dataContext.SetCurrentValueByPath(c.TargetPropertyName, updateList, RtNewtonsoftSerializer.DefaultSerializer);
        await next(dataContext);
    }

    private static bool NoUpdatesForCurrentNode(IDataContext dataContext, CreateUpdateInfoNodeConfiguration c)
    {
        return c.AttributeUpdates!.Where(x => x.ValuePath != null)
            .All(x => dataContext.Current!.SelectToken(x.ValuePath!) == null);
    }
}
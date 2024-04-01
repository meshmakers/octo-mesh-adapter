using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshNodes;
using Meshmakers.Octo.MeshNodes.Nodes;
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

        var list = c.TargetPropertyName == null ?
            new List<IEntityUpdateInfo<RtEntity>>() : 
            dataContext.GetCurrentValueByPath<List<IEntityUpdateInfo<RtEntity>>>(c.TargetPropertyName) ?? new List<IEntityUpdateInfo<RtEntity>>();
        
        
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

            var jToken = dataContext.Current?.SelectToken(au.ValuePath ?? "$");
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
            }

            rtEntity.SetAttributeValue(au.AttributeName, au.AttributeValueType.Value, value);
        }
        list.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId.Value), rtEntity));
      
        dataContext.SetCurrentValueByPath(c.TargetPropertyName, list, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);
    }
}
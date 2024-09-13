using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// This node gets data from mongodb
/// </summary>
[NodeConfiguration(typeof(EnrichWithMongoDataConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class EnrichWithMongoDataNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ILogger<EnrichWithMongoDataNode> logger) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<EnrichWithMongoDataConfiguration>();

        var updateInfos = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (updateInfos != null && updateInfos.Count != 0)
        {
            foreach (var entityUpdateInfo in updateInfos)
            {
                if (entityUpdateInfo.ModOption != EntityModOptions.Update &&
                    entityUpdateInfo.ModOption != EntityModOptions.Replace)
                {
                    continue;
                }
                
                var modOption = entityUpdateInfo.ModOption;
  
                
                var rtId = c.RtId ?? dataContext.GetCurrentValueByPath<OctoObjectId?>(c.RtIdPath ?? "$");
                var ckTypeId = c.CkTypeId ?? dataContext.GetCurrentValueByPath<CkId<CkTypeId>?>(c.CkTypeIdPath ?? "$");
                if (rtId == null)
                {
                    dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtId or RtIdPath is not set");
                    return;
                }
                if (ckTypeId == null)
                {
                    dataContext.Logger.Error(dataContext.NodeStack.Peek(), "CkTypeId or CkTypeIdPath is not set");
                    return;
                }
                
                logger.LogDebug("Processing update ({ModOption}) of update info {CkTypeId}@{RtId}", modOption, ckTypeId, rtId);

                if (rtId != c.RtId || ckTypeId != c.CkTypeId)
                {
                    // different rtId or ckTypeId, continue
                    continue;
                }

                switch (modOption)
                {
                    case EntityModOptions.Replace:
                    case EntityModOptions.Update:
                        await HandleUpdateOrReplace(entityUpdateInfo, c);
                        break;
                    case EntityModOptions.Insert:
                    case EntityModOptions.Delete:
                    default:
                        continue;
                }

                // Updates lt. Broker
            }
        }


        dataContext.SetCurrentValueByPath(c.TargetPath, updateInfos, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);
    }

    private async Task HandleUpdateOrReplace(EntityUpdateInfo<RtEntity> entityUpdateInfo, EnrichWithMongoDataConfiguration config)
    {
        if(config.AttributeUpdates == null || config.AttributeUpdates.Count == 0)
        {
            return;
        }
        
        if (entityUpdateInfo.RtEntity == null)
        {
            return;
        }
        
        var entity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(etlContext.Session, entityUpdateInfo.GetRtEntityId());

        if (entity == null)
        {
            return;
        }
        
        foreach (var attributeUpdate in config.AttributeUpdates)
        {
            var value = entity.GetAttributeValueOrDefault(attributeUpdate.AttributeName!);
            if (value == null)
            {
                continue;
            }
            
            var type = attributeUpdate.AttributeValueType!.Value;
            entityUpdateInfo.RtEntity.SetAttributeValue(attributeUpdate.AttributeName!, type, value);
        }
    }
}
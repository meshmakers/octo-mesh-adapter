using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// This node gets data from mongodb
/// </summary>
[NodeConfiguration(typeof(EnrichWithMongoDataConfiguration))]
internal class EnrichWithMongoDataNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ILogger<EnrichWithMongoDataNode> logger) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var config = dataContext.GetNodeConfiguration<EnrichWithMongoDataConfiguration>();

        var updateInfos = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>(config.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (updateInfos != null && updateInfos.Count != 0)
        {
            foreach (var entityUpdateInfo in updateInfos)
            {
                var rtId = entityUpdateInfo.RtEntityId.RtId;
                var ckTypeId = entityUpdateInfo.RtEntityId.CkTypeId;
                var modOption = entityUpdateInfo.ModOption;

                logger.LogDebug("Processing update ({modOption}) rtId {rtId} and ckTypeId {ckTypeId}", modOption, rtId,
                    ckTypeId);

                if (rtId != config.RtId || ckTypeId != config.CkTypeId)
                {
                    // different rtId or ckTypeId, continue
                    continue;
                }

                switch (modOption)
                {
                    case EntityModOptions.Update:
                    case EntityModOptions.Insert:
                        await HandleInsertOrUpdate(entityUpdateInfo, config);
                        break;
                    case EntityModOptions.Replace:
                    case EntityModOptions.Delete:
                    default:
                        continue;
                }

                // Updates lt. Broker
            }
        }


        dataContext.SetCurrentValueByPath(config.TargetPropertyName, updateInfos, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);
    }

    private async Task HandleInsertOrUpdate(EntityUpdateInfo<RtEntity> entityUpdateInfo, EnrichWithMongoDataConfiguration config)
    {
        if(config.AttributeUpdates == null || config.AttributeUpdates.Count == 0)
        {
            return;
        }
        
        if (entityUpdateInfo.RtEntity == null)
        {
            return;
        }
        
        var entity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(etlContext.Session, entityUpdateInfo.RtEntityId);

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
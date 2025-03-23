using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

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
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<EnrichWithMongoDataConfiguration>();

        var updateInfos = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (updateInfos != null && updateInfos.Count != 0)
        {
            var session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            foreach (var entityUpdateInfo in updateInfos)
            {
                if (entityUpdateInfo.ModOption != EntityModOptions.Update &&
                    entityUpdateInfo.ModOption != EntityModOptions.Replace)
                {
                    continue;
                }

                var modOption = entityUpdateInfo.ModOption;


                var rtId = c.RtId ?? dataContext.GetSimpleValueByPath<OctoObjectId?>(c.RtIdPath ?? "$");
                var ckTypeId = c.CkTypeId ?? dataContext.GetSimpleValueByPath<CkId<CkTypeId>?>(c.CkTypeIdPath ?? "$");
                if (rtId == null)
                {
                    nodeContext.Error("RtId or RtIdPath is not set");
                    return;
                }

                if (ckTypeId == null)
                {
                    nodeContext.Error("CkTypeId or CkTypeIdPath is not set");
                    return;
                }

                logger.LogDebug("Processing update ({ModOption}) of update info {CkTypeId}@{RtId}", modOption, ckTypeId,
                    rtId);

                if (rtId != c.RtId || ckTypeId != c.CkTypeId)
                {
                    // different rtId or ckTypeId, continue
                    continue;
                }

                switch (modOption)
                {
                    case EntityModOptions.Replace:
                    case EntityModOptions.Update:
                        await HandleUpdateOrReplace(session, entityUpdateInfo, c);
                        break;
                    case EntityModOptions.Insert:
                    case EntityModOptions.Delete:
                    default:
                        continue;
                }

                // Updates lt. Broker
            }

            await session.CommitTransactionAsync();
        }


        dataContext.SetValueByPath(c.TargetPath, updateInfos, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode,
            RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private async Task HandleUpdateOrReplace(IOctoSession session, EntityUpdateInfo<RtEntity> entityUpdateInfo,
        EnrichWithMongoDataConfiguration config)
    {
        if (config.AttributeUpdates == null || config.AttributeUpdates.Count == 0)
        {
            return;
        }

        if (entityUpdateInfo.RtEntity == null)
        {
            return;
        }

        var entity =
            await etlContext.TenantRepository.GetRtEntityByRtIdAsync(session, entityUpdateInfo.GetRtEntityId());

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
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshNodes;
using Meshmakers.Octo.MeshNodes.Nodes;
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
internal class EnrichWithMongoDataNode(NodeDelegate next, IMeshEtlContext etlContext, ILogger<EnrichWithMongoDataNode> logger) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var config = dataContext.GetNodeConfiguration<EnrichWithMongoDataConfiguration>();
        
        
        logger.LogInformation("We do like we use the context, so the compiler doesn't complain {}", etlContext.TenantId);
        
        
        var list = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>(config.Path, RtNewtonsoftSerializer.DefaultSerializer);

        if (list != null && list.Any())
        {
            foreach (var entityUpdateInfo in list)
            {
                var rtId = entityUpdateInfo.RtEntityId.RtId;
                var ckTypeId = entityUpdateInfo.RtEntityId.CkTypeId;
                var modOption = entityUpdateInfo.ModOption;
                
                if(rtId != config.RtId || ckTypeId != config.CkTypeId || config.AttributeUpdates == null)
                {
                    // different rtId or ckTypeId, continue
                    continue;
                }
                
                
                

                switch (modOption)
                {
                    case EntityModOptions.Update:
                    case EntityModOptions.Insert:
                        if (entityUpdateInfo.RtEntity != null)
                        {
                            var entity =
                                await etlContext.TenantRepository.GetRtEntityByRtIdAsync(etlContext.Session,
                                    entityUpdateInfo.RtEntityId);

                            if (entity != null)
                            {
                                foreach(var attributeUpdate in config.AttributeUpdates)
                                {
                                    var value = entity.GetAttributeValueOrDefault(attributeUpdate.AttributeName!);
                                    switch (attributeUpdate.AttributeValueType)
                                    {
                                        case AttributeValueTypesDto.Int:
                                            break;
                                        case AttributeValueTypesDto.String:
                                            break;
                                        case AttributeValueTypesDto.Binary:
                                            break;
                                        case AttributeValueTypesDto.Boolean:
                                            break;
                                        case AttributeValueTypesDto.DateTime:
                                            break;
                                        case AttributeValueTypesDto.Double:
                                            entityUpdateInfo.RtEntity.SetAttributeValue(attributeUpdate.AttributeName!,
                                                AttributeValueTypesDto.Double, value);
                                            break;
                                        case AttributeValueTypesDto.StringArray:
                                            break;
                                        case AttributeValueTypesDto.IntArray:
                                            break;
                                        case AttributeValueTypesDto.BinaryLinked:
                                            break;
                                        case AttributeValueTypesDto.Record:
                                            break;
                                        case AttributeValueTypesDto.RecordArray:
                                            break;
                                        case AttributeValueTypesDto.TimeSpan:
                                            break;
                                        case AttributeValueTypesDto.Enum:
                                            break;
                                        case AttributeValueTypesDto.Int64:
                                            break;
                                        case AttributeValueTypesDto.DateTimeOffset:
                                            break;
                                        case null:
                                            break;
                                        default:
                                            throw new ArgumentOutOfRangeException();
                                    }
                                    
                                    
                                    // entity.SetAttributeValue(attributeUpdate.ValuePath, attributeUpdate.AttributeValueType, attributeUpdate.);
                                    //
                                    //
                                    // var value = dataContext.GetCurrentValueByPath(attributeUpdate.ValuePath, RtNewtonsoftSerializer.DefaultSerializer);
                                    // if (value != null)
                                    // {
                                    //     entity.Attributes[attributeUpdate.AttributeName] = value;
                                    // }
                                    logger.LogInformation("hjallo2");
                                }
                            }
                        }

                        break;

                    
                    case EntityModOptions.Replace:
                    case EntityModOptions.Delete:
                    default:
                        continue;
                }
                
                // Updates lt. Broker
   
            }
        }

        // DataPointDto? dataPoint;
        // if (dataString == null)
        // {
        //     throw PipelineDataException.CouldNotDeserializeData(null);
        // }
        //
        // try
        // {
        //     dataPoint = JsonSerializer.Deserialize<DataPointDto>(dataString)!;
        // }
        // catch
        // {
        //     throw PipelineDataException.CouldNotDeserializeData(dataString);
        // }

        dataContext.SetCurrentValueByPath(config.TargetPropertyName,list, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext);

    }
}



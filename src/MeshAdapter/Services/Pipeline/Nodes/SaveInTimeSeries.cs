using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Services.Common.StreamData;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

[NodeConfiguration(typeof(SaveInTimeSeriesNodeConfiguration))]
internal class SaveInTimeSeriesNode(NodeDelegate next, IMeshEtlContext etlContext, IStreamDataDatabaseClient streamDataDatabaseClient)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var data = dataContext.DeserializeCurrentValue<List<EntityUpdateInfo<RtEntity>>>("$._UpdateItems",
            RtNewtonsoftSerializer.DefaultSerializer);
        

        if (data != null && data.Count != 0)
        {
            var tenantId = etlContext.TenantId;

            foreach (var datapoint in data)
            {
                if (datapoint.RtEntity == null)
                {
                    continue;
                }

                switch (datapoint.ModOption)
                {
                    case EntityModOptions.Insert:
                    case EntityModOptions.Update:
                        var dataPointDto = new DataPointDto(datapoint.RtEntity.Attributes.ToDictionary())
                        {
                            AdapterReceivedTimestamp = etlContext.TransactionStartedDateTime,
                            Timestamp = etlContext.ExternalReceivedDateTime ?? etlContext.TransactionStartedDateTime,
                            ExternalId = OctoObjectId.Empty,
                            PlugId = OctoObjectId.Empty,
                            RtId = datapoint.RtEntityId.RtId,
                            CkTypeId = datapoint.RtEntityId.CkTypeId,
                        };


                        await streamDataDatabaseClient.InsertDataAsync(tenantId, dataPointDto);

                        break;


                    // we don't delete data that comes over the broker
                    case EntityModOptions.Delete:
                    case EntityModOptions.Replace:
                    default:
                        break;
                }
            }
        }
        
        await next(dataContext);
    }
}
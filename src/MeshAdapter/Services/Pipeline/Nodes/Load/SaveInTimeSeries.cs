using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Services.Common.StreamData;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

[NodeConfiguration(typeof(SaveInTimeSeriesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
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
            
            var toInsert = new List<DataPointDto>();

            foreach (var datapoint in data)
            {
                if (datapoint.RtEntity == null)
                {
                    continue;
                }

                switch (datapoint.ModOption)
                {
                    case EntityModOptions.Replace:
                    case EntityModOptions.Update:
                        var dataPointDto = new DataPointDto(datapoint.RtEntity.Attributes.ToDictionary())
                        {
                            AdapterReceivedTimestamp = etlContext.TransactionStartedDateTime,
                            Timestamp = datapoint.RtEntity.RtChangedDateTime ?? etlContext.ExternalReceivedDateTime ?? etlContext.TransactionStartedDateTime,
                            ExternalId = OctoObjectId.Empty,
                            PlugId = OctoObjectId.Empty,
                            RtId = datapoint.RtId,
                            CkTypeId = datapoint.CkTypeId,
                        };

                        toInsert.Add(dataPointDto);
                        break;

                    // we don't delete data that comes over the broker
                    case EntityModOptions.Delete:
                    case EntityModOptions.Insert:
                    default:
                        break;
                }
            }

            if (toInsert.Count != 0)
            {
                await streamDataDatabaseClient.InsertDataAsync(tenantId, toInsert);
            }
        }
        
        await next(dataContext);
    }
}
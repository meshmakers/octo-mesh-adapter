using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Services.StreamData;
using Meshmakers.Octo.Services.StreamData.Dtos;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

[NodeConfiguration(typeof(SaveInTimeSeriesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SaveInTimeSeriesNode(NodeDelegate next, IMeshEtlContext etlContext, IStreamDataDatabaseClient streamDataDatabaseClient)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SaveInTimeSeriesNodeConfiguration>();
        
        var data = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
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
                    case EntityModOptions.Insert:
                        var dataPointDto = new DataPointDto(datapoint.RtEntity.Attributes.ToDictionary())
                        {
                            Timestamp = datapoint.RtEntity.RtChangedDateTime ?? etlContext.ExternalReceivedDateTime ?? etlContext.TransactionStartedDateTime,
                            RtId = datapoint.RtId,
                            RtWellKnownName = datapoint.RtEntity.RtWellKnownName,
                            CkTypeId = datapoint.CkTypeId,
                        };

                        toInsert.Add(dataPointDto);
                        break;

                    // we don't delete data that comes over the broker
                    case EntityModOptions.Delete:
                    default:
                        break;
                }
            }

            if (toInsert.Count != 0)
            {
                nodeContext.Debug($"Inserting {toInsert.Count} data points into the stream data database");
                await streamDataDatabaseClient.InsertDataAsync(tenantId, toInsert);
            }
        }
        else
        {
            nodeContext.Warning("No update infos found");
        }

        
        await next(dataContext, nodeContext);
    }
}

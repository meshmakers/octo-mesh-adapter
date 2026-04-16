using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

[NodeConfiguration(typeof(SaveInTimeSeriesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SaveInTimeSeriesNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext)
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

            // Get the stream data repository via the engine tenant context
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
            var streamDataRepo = tenantContext.GetStreamDataRepository()
                ?? throw new InvalidOperationException(
                    $"Stream data repository is not available for tenant '{tenantId}'. " +
                    "Ensure AddCrateDbStreamDataRepository() was called during startup.");

            await streamDataRepo.EnsureDatabaseCreatedAsync();

            var toInsert = new List<StreamDataPoint>();

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
                        // datapoint.RtId is null for inserts (no ID assigned yet by repository)
                        // so we fall back to the entity's own RtId.
                        var rtId = datapoint.RtId ?? datapoint.RtEntity.RtId;

                        var streamDataPoint = new StreamDataPoint
                        {
                            Timestamp = datapoint.RtEntity.RtChangedDateTime
                                ?? etlContext.ExternalReceivedDateTime
                                ?? etlContext.TransactionStartedDateTime,
                            RtId = rtId,
                            RtWellKnownName = datapoint.RtEntity.RtWellKnownName,
                            CkTypeId = datapoint.CkTypeId,
                            Attributes = datapoint.RtEntity.Attributes.ToDictionary()
                        };

                        toInsert.Add(streamDataPoint);
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
                await streamDataRepo.InsertAsync(toInsert);
            }
        }
        else
        {
            nodeContext.Warning("No update infos found");
        }

        await next(dataContext, nodeContext);
    }
}

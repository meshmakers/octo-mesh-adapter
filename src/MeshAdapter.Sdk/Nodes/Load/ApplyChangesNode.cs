using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using MongoDB.Driver;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[NodeConfiguration(typeof(ApplyChangesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ApplyChangesNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private static readonly SemaphoreSlim ApplySemaphoreSlim = new(1, 1);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ApplyChangesNodeConfiguration>();

        var list = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (list != null && list.Any())
        {
            // We use all inserts
            var resultUpdateInfos = list.Where(x => x.ModOption == EntityModOptions.Insert).ToList();

            // first we reverse the list because we are interested in the last update for each entity.
            var tempList = list.Where(x => x.ModOption != EntityModOptions.Insert).Reverse();

            // then we are throwing away duplicates because we only want to update each entity once.
            resultUpdateInfos.AddRange(tempList.DistinctBy(x => x.GetRtEntityId()));

            try
            {
                // We need to use a semaphore here, because there is a chance that multiple pipelines are running at the same time and that results
                // that we come to this behavior. https://www.mongodb.com/community/forums/t/mongoservererror-writeconflict-error-this-operation-conflicted-with-another-operation-please-retry-your-operation-or-multi-document-transaction/206298/7
                // There is a timeout of 5 milliseconds to lock transactions - so we need to make sure that we are not running into this issue.
                await ApplySemaphoreSlim.WaitAsync();

                // Retry this 5 times with a delay of 1 second
                int count = 0;
                while (count <= 5)
                {
                    count++;
                    try
                    {
                        var session = await etlContext.TenantRepository.GetSessionAsync();
                        session.StartTransaction();

                        OperationResult operationResult = new();
                        await etlContext.TenantRepository.ApplyChangesAsync(session, resultUpdateInfos,
                            operationResult);
                        if (operationResult.HasErrors || operationResult.HasFatalErrors)
                        {
                            nodeContext.Error("Error updating RtEntity");
                            await session.AbortTransactionAsync();
                        }
                        else
                        {
                            await session.CommitTransactionAsync();
                        }
                    }
                    catch (MongoCommandException e)
                    {
                        if (e.Code == 112) // Indicates write conflict
                        {
                            continue;
                        }

                        throw;
                    }

                    break;
                }
            }
            finally
            {
                ApplySemaphoreSlim.Release();
            }
        }
        else
        {
            nodeContext.Warning("No update infos found");
        }


        await next(dataContext, nodeContext);
    }
}
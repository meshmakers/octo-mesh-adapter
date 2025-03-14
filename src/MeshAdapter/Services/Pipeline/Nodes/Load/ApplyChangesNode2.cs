using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using MongoDB.Driver;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[NodeConfiguration(typeof(ApplyChangesNodeConfiguration2))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ApplyChangesNode2(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private static readonly SemaphoreSlim ApplySemaphoreSlim = new(1, 1);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ApplyChangesNodeConfiguration2>();

        List<EntityUpdateInfo<RtEntity>> entityUpdates = [];
        List<AssociationUpdateInfo> associationUpdates = [];
        if (c.EntityUpdatesPath != null)
        {
            entityUpdates = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.EntityUpdatesPath,
                RtNewtonsoftSerializer.DefaultSerializer) ?? [];
        }

        if (c.AssociationUpdatesPath != null)
        {
            associationUpdates = dataContext.GetComplexObjectByPath<List<AssociationUpdateInfo>>(
                c.AssociationUpdatesPath,
                RtNewtonsoftSerializer.DefaultSerializer) ?? [];
        }

        if (entityUpdates.Any() || associationUpdates.Any())
        {
            // We use all inserts
            var resultUpdateInfos = entityUpdates.Where(x => x.ModOption == EntityModOptions.Insert).ToList();
            var resultAssocUpdate =
                associationUpdates.Where(x => x.ModOption == AssociationModOptionsDto.Create).ToList();

            // first we reverse the list because we are interested in the last update for each entity.
            var tempEntities = entityUpdates.Where(x => x.ModOption != EntityModOptions.Insert).Reverse();
            var tempAssoc = associationUpdates.Where(x => x.ModOption != AssociationModOptionsDto.Create).Reverse();

            // then we are throwing away duplicates because we only want to update each entity once.
            resultUpdateInfos.AddRange(tempEntities.DistinctBy(x => x.GetRtEntityId()));
            resultAssocUpdate.AddRange(tempAssoc.DistinctBy(ConcatOriginAndTarget));

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
                            resultAssocUpdate, operationResult);
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
    
    private static string ConcatOriginAndTarget(AssociationUpdateInfo updateInfo)
    {
        return string.Format($"{updateInfo.Origin}{updateInfo.Target}");
    }
}
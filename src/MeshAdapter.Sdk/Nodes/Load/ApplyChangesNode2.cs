using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using MongoDB.Driver;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

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
            entityUpdates = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(c.EntityUpdatesPath) ?? [];
        }

        if (c.AssociationUpdatesPath != null)
        {
            associationUpdates = dataContext.Get<List<AssociationUpdateInfo>>(
                c.AssociationUpdatesPath) ?? [];
        }

        if (entityUpdates.Any() || associationUpdates.Any())
        {
            // We use all inserts
            var resultUpdateInfos = entityUpdates.Where(x => x.ModOption == EntityModOptions.Insert).ToList();
            var resultAssocUpdate =
                associationUpdates.Where(x => x.ModOption == AssociationModOptionsDto.Create).ToList();

            // Merge multiple update-items for the same entity into a single update
            // by combining their attribute changes. Later updates win on attribute conflicts.
            var nonInsertUpdates = entityUpdates.Where(x => x.ModOption != EntityModOptions.Insert);
            var mergedByEntity = nonInsertUpdates
                .GroupBy(x => x.GetRtEntityId())
                .Select(g => MergeEntityUpdates(g.ToList(), nodeContext));
            resultUpdateInfos.AddRange(mergedByEntity);

            // Associations: dedupe by origin+target (last one wins)
            var tempAssoc = associationUpdates.Where(x => x.ModOption != AssociationModOptionsDto.Create).Reverse();
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

    /// <summary>
    /// Merges multiple EntityUpdateInfo instances for the same entity into a single update
    /// by combining their attribute changes. Later updates win on attribute conflicts.
    /// All updates in <paramref name="updates"/> must target the same entity.
    /// </summary>
    private static EntityUpdateInfo<RtEntity> MergeEntityUpdates(IReadOnlyList<EntityUpdateInfo<RtEntity>> updates,
        INodeContext nodeContext)
    {
        if (updates.Count == 1)
        {
            return updates[0];
        }

        var last = updates[^1];
        var lastEntity = last.RtEntity;
        if (lastEntity == null) return last;

        // Merge all attributes — later updates win on conflicts
        var mergedAttributes = new Dictionary<string, object?>();
        foreach (var update in updates)
        {
            if (update.RtEntity == null) continue;
            foreach (var kvp in update.RtEntity.Attributes)
            {
                if (mergedAttributes.TryGetValue(kvp.Key, out var existing) && !Equals(existing, kvp.Value))
                {
                    nodeContext.Warning(
                        $"Merging entity {last.GetRtEntityId()}: attribute '{kvp.Key}' has conflicting values " +
                        $"('{existing}' vs '{kvp.Value}') — later value wins");
                }
                mergedAttributes[kvp.Key] = kvp.Value;
            }
        }

        var mergedEntity = new RtEntity(lastEntity.CkTypeId!, last.GetRtEntityId().RtId, mergedAttributes)
        {
            RtChangedDateTime = lastEntity.RtChangedDateTime,
            RtWellKnownName = lastEntity.RtWellKnownName
        };

        return EntityUpdateInfo<RtEntity>.CreateUpdate(last.GetRtEntityId(), mergedEntity);
    }
}
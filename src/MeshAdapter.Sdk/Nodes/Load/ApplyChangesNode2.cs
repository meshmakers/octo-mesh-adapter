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

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.ApplyChanges2, new
            {
                entityUpdatesPath = c.EntityUpdatesPath,
                associationUpdatesPath = c.AssociationUpdatesPath,
                entityUpdateCount = entityUpdates.Count,
                associationUpdateCount = associationUpdates.Count,
                wouldApplyEntityUpdates = entityUpdates,
                wouldApplyAssociationUpdates = associationUpdates
            });
            await next(dataContext, nodeContext);
            return;
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
                    IOctoSession? session = null;
                    try
                    {
                        session = await etlContext.TenantRepository.GetSessionAsync();
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
                    catch (Exception e) when (
                        c.OnDuplicateKey == DuplicateKeyHandling.Report && IsDuplicateKey(e))
                    {
                        // A unique index refused the write. Not a retry case: the conflicting
                        // document is committed, so every attempt would fail the same way. The
                        // pipeline asked to hear about it rather than to fail, so roll back and
                        // hand it a flag - see DuplicateKeyHandling.Report.
                        if (session != null)
                        {
                            await session.AbortTransactionAsync();
                        }

                        nodeContext.Warning(
                            $"A unique index refused the write, reporting it as configured: {DescribeDuplicates(e)}");

                        if (c.DuplicateKeyTargetPath != null)
                        {
                            dataContext.Set(c.DuplicateKeyTargetPath, true, DocumentModes.Extend,
                                ValueKinds.Simple, TargetValueWriteModes.Overwrite);
                        }
                        else
                        {
                            nodeContext.Warning(
                                "OnDuplicateKey is Report but no DuplicateKeyTargetPath is configured, so the " +
                                "pipeline cannot tell the refusal from a successful write.");
                        }
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
    
    /// <summary>
    /// Walks the inner-exception chain: the repository wraps the driver failure in an
    /// OperationFailedException, so the bulk-write exception is never the one that arrives here.
    /// </summary>
    private static bool IsDuplicateKey(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is MongoBulkWriteException bulk &&
                bulk.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
            {
                return true;
            }

            if (e is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The index and key that collided, for the log. The document itself is not written out: on a
    /// public endpoint it is the caller's own payload and has no business in our logs twice.
    /// </summary>
    private static string DescribeDuplicates(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is MongoBulkWriteException bulk)
            {
                return string.Join("; ", bulk.WriteErrors
                    .Where(error => error.Category == ServerErrorCategory.DuplicateKey)
                    .Select(error => error.Message));
            }

            if (e is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } single)
            {
                return single.WriteError.Message;
            }
        }

        return "no duplicate-key detail found in the exception chain";
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
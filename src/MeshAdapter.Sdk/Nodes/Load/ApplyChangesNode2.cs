using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
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
                        // AB#4975 / AB#5028 — scoped: the engine stamps RtCreatedBy and enforces data
                        // permissions for whoever the execution acts as. The AB#4975 branch that fell
                        // back to a system session without a verified caller is gone: the fallback is
                        // now the adapter's service account and only then the system context, decided
                        // once per execution rather than here (AB#5027 / AB#5028).
                        //
                        // Assigned rather than declared here: the duplicate-key catch below rolls the
                        // transaction back, so the session has to outlive this block (AB#3717).
                        session = await etlContext.GetSessionForAsync(c.Identity);
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

                            // Written on every run, not only on a duplicate: the node can run more
                            // than once against one data document (a ForEach over several records,
                            // or several apply steps), and a true left by an earlier iteration
                            // would answer "already on file" for a record this run just stored.
                            if (CanReportDuplicateKey(c))
                            {
                                dataContext.Set(c.DuplicateKeyTargetPath!, false, DocumentModes.Extend,
                                    ValueKinds.Simple, TargetValueWriteModes.Overwrite);
                            }
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
                    catch (Exception e) when (CanReportDuplicateKey(c) && IsDuplicateKey(e))
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

                        dataContext.Set(c.DuplicateKeyTargetPath!, true, DocumentModes.Extend,
                            ValueKinds.Simple, TargetValueWriteModes.Overwrite);
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
    /// Whether a duplicate key can be reported rather than thrown. Report means "do not fail, tell
    /// me through the flag", so without somewhere to put the flag there is nothing to tell: the
    /// caller would get neither the failure nor the signal, and a rolled-back write would be
    /// indistinguishable from a stored one. In that case the filter does not match and the
    /// exception keeps travelling, which is what this node has always done.
    ///
    /// A blank path counts as no path. An empty JSONPath addresses the document ROOT in this data
    /// context (DataContext.Set treats "" and "$" alike), so `duplicateKeyTargetPath: ""` would
    /// write the flag over the whole data document instead of into a field of it.
    /// </summary>
    internal static bool CanReportDuplicateKey(ApplyChangesNodeConfiguration2 c)
    {
        return c.OnDuplicateKey == DuplicateKeyHandling.Report
               && !string.IsNullOrWhiteSpace(c.DuplicateKeyTargetPath);
    }

    /// <summary>
    /// The index that refused the write, for the log, and never the value that collided. MongoDB
    /// puts the duplicate key itself in the message, and the unique keys this node guards are
    /// business identifiers - an applicant's e-mail address on the public registration route, for
    /// one. The index name is what an operator needs to find the conflict; the value is theirs to
    /// look up under whatever access control the data sits behind.
    /// </summary>
    private static string DescribeDuplicates(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is MongoBulkWriteException bulk)
            {
                var names = bulk.WriteErrors
                    .Where(error => error.Category == ServerErrorCategory.DuplicateKey)
                    .Select(error => IndexNameOf(error.Message))
                    .ToArray();
                return names.Length > 0 ? string.Join("; ", names) : "a unique index";
            }

            if (e is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } single)
            {
                return IndexNameOf(single.WriteError.Message);
            }
        }

        return "no duplicate-key detail found in the exception chain";
    }

    /// <summary>
    /// Pulls the index name out of a MongoDB duplicate-key message and drops the rest, which is
    /// where the colliding value sits. Anything unrecognised degrades to a constant rather than
    /// to the original text, so a message shape we have not seen cannot leak by default.
    /// </summary>
    internal static string IndexNameOf(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "a unique index";
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"index:\s*(?<name>[^\s]+)");
        return match.Success ? $"index {match.Groups["name"].Value}" : "a unique index";
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
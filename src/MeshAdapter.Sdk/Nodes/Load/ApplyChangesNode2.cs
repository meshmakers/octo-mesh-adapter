using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using MongoDB.Driver;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[NodeConfiguration(typeof(ApplyChangesNodeConfiguration2))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ApplyChangesNode2(NodeDelegate next, IMeshEtlContext etlContext, ICkCacheService ckCacheService) : IPipelineNode
{
    private static readonly SemaphoreSlim ApplySemaphoreSlim = new(1, 1);

    /// <summary>
    /// How often the node tries a write that meets a WriteConflict (112) before it gives up (AB#9466).
    /// </summary>
    internal const int MaxWriteAttempts = 6;

    private const int WriteConflictBackoffBaseMs = 25;
    private const int WriteConflictBackoffCapMs = 400;

    /// <summary>MongoDB's error code for a WriteConflict in a transaction.</summary>
    private const int WriteConflictCode = 112;

    /// <summary>The failure code of a target whose engine validation refused the batch (AB#9770).</summary>
    internal const string ValidationFailedCode = "VALIDATION_FAILED";

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

        // AB#9770: the one-way master-data sync into the child tenants has its own path (one transaction per target,
        // upsert keyed by RtSyncOrigin, fail after all targets).
        if (c.TargetTenants != null)
        {
            await SyncIntoTargetsAsync(c, entityUpdates, dataContext, nodeContext);
            await next(dataContext, nodeContext);
            return;
        }

        // AB#9769: with a target tenant, the cross-tenant guard opens it first; a refusal (CROSS_TENANT_FORBIDDEN) ends
        // the node here, before any check reads the target and before anything is written, also in a dry run.
        ICrossTenantAccess? target = null;
        if (c.TargetTenant != null)
        {
            target = await etlContext.OpenTenantAsync(c.TargetTenant, c.Identity, dataContext, nodeContext);
        }

        var repository = target?.Repository ?? etlContext.TenantRepository;

        // AB#7463: inserts of a type whose CK method replaces the generic create are refused outside that
        // method's own run. Checked on the raw entity updates (every insert is part of the later
        // resultUpdateInfos), before the dry-run branch and before any write; the whole batch is refused.
        // AB#8059: the same call refuses identity-owned User attributes and identity association edges (insert and
        // delete) outside the method run of a System.Identity type.
        // AB#9769: a batch for another tenant is judged with that tenant's CK model and without a method run.
        await RtAttributeAccessGuard.EnsureWritesAllowedAsync(
            target == null ? WriteGuardScope.Own(etlContext) : WriteGuardScope.CrossTenant(etlContext, target),
            ckCacheService, entityUpdates, associationUpdates);

        // AB#9769: an association written into another tenant joins two entities of that tenant only.
        if (target != null)
        {
            await EnsureAssociationEndsInTargetAsync(target, c.TargetTenant!, entityUpdates, associationUpdates,
                nodeContext);
        }

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.ApplyChanges2, new
            {
                entityUpdatesPath = c.EntityUpdatesPath,
                associationUpdatesPath = c.AssociationUpdatesPath,
                targetTenantId = target?.TenantId,
                entityUpdateCount = entityUpdates.Count,
                recordArrayItemUpdateCount = entityUpdates.Sum(u => u.RecordArrayItemUpdates?.Count ?? 0),
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

                // AB#9466: a WriteConflict (112) is retried with a short jittered backoff, on a fresh session after
                // the failed transaction was aborted. The semaphore above serialises the writes of THIS process only;
                // a second adapter replica writing the same document or unique key in parallel still meets 112.
                for (var attempt = 1; ; attempt++)
                {
                    IOctoSession? session = null;
                    try
                    {
                        // AB#4975 / AB#5028 — scoped: the engine stamps RtCreatedBy and enforces data
                        // permissions for whoever the execution acts as. The AB#4975 branch that fell
                        // back to a system session without a verified caller is gone: the fallback is
                        // now the adapter's service account and only then the system context, decided
                        // once per execution rather than here (AB#5027 / AB#5028).
                        //
                        // Assigned rather than declared here: the catch blocks below roll the
                        // transaction back, so the session has to outlive this block (AB#3717).
                        //
                        // AB#9769: with a target, the session the guard opened under the identity proven there
                        // (never this node's own session, the source identity or the system context).
                        session = target != null
                            ? await target.OpenSessionAsync()
                            : await etlContext.GetSessionForAsync(c.Identity);
                        session.StartTransaction();

                        // AB#9770: the engine refuses a change of synced master data (an entity carrying
                        // RtSyncOrigin) and every write of RtSyncOrigin: only the sync of its source tenant writes them.
                        OperationResult operationResult = new();
                        try
                        {
                            await SyncOriginRefusal.RunAsync(nodeContext.NodePath.ToString(), () =>
                                repository.ApplyChangesAsync(session, resultUpdateInfos, resultAssocUpdate,
                                    operationResult));
                        }
                        catch (SyncOriginWriteRefusedException)
                        {
                            await AbortQuietlyAsync(session, nodeContext);
                            throw;
                        }
                        if (operationResult.HasErrors || operationResult.HasFatalErrors)
                        {
                            nodeContext.Error("Error updating RtEntity");
                            await session.AbortTransactionAsync();

                            // AB#9770 (decision 6): a write into another tenant that its engine refused fails the
                            // execution and names the target, instead of only logging. The own tenant keeps the
                            // logging-only behaviour it always had.
                            if (target != null)
                            {
                                nodeContext.Error("The write into tenant '{0}' was refused by its validation: {1}",
                                    target.TenantId, operationResult.GetMessages());
                                throw CrossTenantWriteFailedException.Create(nodeContext.NodePath.ToString(), 1,
                                    [new CrossTenantWriteFailure(target.TenantId, ValidationFailedCode)], []);
                            }
                        }
                        else
                        {
                            await session.CommitTransactionAsync();

                            // AB#7442: an atomic RecordArray item update that matched no item is not an error; the
                            // engine reports it as a warning and writes nothing for it.
                            foreach (var warning in operationResult.Messages.Where(m =>
                                         m.MessageLevel == MessageLevel.Warning))
                            {
                                nodeContext.Warning("{0}", warning.ToString());
                            }

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
                    catch (Exception e) when (CanReportDuplicateKey(c) && IsDuplicateKey(e))
                    {
                        // A unique index refused the write. Not a retry case: the conflicting
                        // document is committed, so every attempt would fail the same way. The
                        // pipeline asked to hear about it rather than to fail, so roll back and
                        // hand it a flag - see DuplicateKeyHandling.Report.
                        await AbortQuietlyAsync(session, nodeContext);

                        nodeContext.Warning(
                            $"A unique index refused the write, reporting it as configured: {DescribeDuplicates(e)}");

                        dataContext.Set(c.DuplicateKeyTargetPath!, true, DocumentModes.Extend,
                            ValueKinds.Simple, TargetValueWriteModes.Overwrite);
                    }
                    catch (Exception e) when (IsWriteConflict(e))
                    {
                        // AB#9466: another transaction holds the same document or unique key - typically a second
                        // adapter replica that has not committed yet. The server has already aborted this
                        // transaction; abort it on our side too, so the next attempt starts clean.
                        await AbortQuietlyAsync(session, nodeContext);

                        if (attempt < MaxWriteAttempts)
                        {
                            var delay = WriteConflictBackoff(attempt, Random.Shared);
                            nodeContext.Debug(
                                $"Write conflict (attempt {attempt} of {MaxWriteAttempts}), retrying in {delay.TotalMilliseconds:F0} ms");
                            session?.Dispose();
                            session = null;

                            // Wait without the process-wide lock, so the other ApplyChanges@2 runs of this
                            // adapter (every pipeline and tenant) are not held up by one conflicting write.
                            // Re-acquired before the next attempt; the outer finally releases it once.
                            ApplySemaphoreSlim.Release();
                            try
                            {
                                await Task.Delay(delay);
                            }
                            finally
                            {
                                await ApplySemaphoreSlim.WaitAsync();
                            }

                            continue;
                        }

                        if (!CanReportDuplicateKey(c))
                        {
                            // Without Report the node fails, as it does for a duplicate key: nothing was
                            // written, so the pipeline must not carry on as if it had been.
                            throw;
                        }

                        // Report: the other writer kept the key for the whole retry window. Nothing was
                        // written, so this is a conflict and never a silent success.
                        nodeContext.Warning(
                            $"A write conflict persisted over {MaxWriteAttempts} attempts, reporting it as a conflict as configured");

                        dataContext.Set(c.DuplicateKeyTargetPath!, true, DocumentModes.Extend,
                            ValueKinds.Simple, TargetValueWriteModes.Overwrite);
                    }
                    finally
                    {
                        session?.Dispose();
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

    // ------------------------------------------------------------------------------------------------ sync (AB#9770)

    /// <summary>
    /// AB#9770: writes the batch as a one-way master-data sync into every target tenant of
    /// <see cref="ApplyChangesNodeConfiguration2.TargetTenants" />. Each target is opened through the cross-tenant guard
    /// and written in its own transaction; a refused or failed target does not stop the others, and the node throws
    /// <see cref="CrossTenantWriteFailedException" /> after the last target, naming every failed one.
    /// </summary>
    private async Task SyncIntoTargetsAsync(ApplyChangesNodeConfiguration2 c,
        IReadOnlyList<EntityUpdateInfo<RtEntity>> entityUpdates, IDataContext dataContext, INodeContext nodeContext)
    {
        var nodePath = nodeContext.NodePath.ToString();
        var problem = CheckSyncConfiguration(c);
        if (problem != null)
        {
            throw CrossTenantAccessDeniedException.InvalidConfiguration(nodePath, problem);
        }

        var items = SyncItem.From(entityUpdates, nodePath);

        IReadOnlyList<string> targets = c.TargetTenants!.TenantIds != null
            ? c.TargetTenants.TenantIds.Select(TenantRegistrySnapshot.Normalize).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : await etlContext.ListChildTenantsAsync(nodeContext);
        if (targets.Count == 0)
        {
            nodeContext.Warning("The sync has no target tenant (the pipeline's tenant has no child tenant)");
            return;
        }

        var sourceTenantId = etlContext.TenantId;
        var syncedAt = DateTime.UtcNow;
        var isDryRun = nodeContext.PipelineExecutionMode?.IsDryRun == true;
        var failures = new List<CrossTenantWriteFailure>();
        var written = new List<string>();
        var dryRunPlans = new List<object>();

        foreach (var tenantId in targets)
        {
            try
            {
                // Every target, also one from the registry's list of children, passes the whole guard: relation,
                // identity proof in that tenant, generation.
                var access = await etlContext.OpenTenantAsync(
                    new TenantTargetConfiguration { Relation = TenantRelation.Child, TenantId = tenantId },
                    c.Identity, dataContext, nodeContext);
                var plan = await SyncIntoTargetAsync(access, items, sourceTenantId, syncedAt, isDryRun, nodeContext);
                written.Add(access.TenantId);
                if (isDryRun)
                {
                    dryRunPlans.Add(new
                    {
                        targetTenantId = access.TenantId,
                        inserts = plan.Count(u => u.ModOption == EntityModOptions.Insert),
                        updates = plan.Count(u => u.ModOption == EntityModOptions.Update),
                        deletes = plan.Count(u => u.ModOption == EntityModOptions.Delete),
                        wouldApplyEntityUpdates = plan
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var code = FailureCode(ex);
                failures.Add(new CrossTenantWriteFailure(tenantId, code) { Cause = ex });
                // The type and the stable code only: a driver message can carry a key value of the target.
                nodeContext.Warning($"The sync into tenant '{tenantId}' failed ({code}, {ex.GetType().Name}); " +
                                    "the other targets are written");
            }
        }

        if (isDryRun)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.ApplyChanges2, new
            {
                entityUpdatesPath = c.EntityUpdatesPath,
                syncTargets = dryRunPlans,
                failedTargets = failures
            });
        }

        if (failures.Count > 0)
        {
            throw CrossTenantWriteFailedException.Create(nodePath, targets.Count, failures, written);
        }
    }

    /// <summary>The static rules of a sync configuration; <c>null</c> when consistent.</summary>
    internal static string? CheckSyncConfiguration(ApplyChangesNodeConfiguration2 c)
    {
        var set = c.TargetTenants!;
        if (c.TargetTenant != null)
        {
            return "set either targetTenant or targetTenants, not both";
        }

        if (set.Relation == null)
        {
            return "targetTenants needs a relation (Child)";
        }

        if (set.Relation != TenantRelation.Child)
        {
            return $"targetTenants supports only the relation Child, not {set.Relation}";
        }

        if (set.TenantIds is { Count: 0 } || set.TenantIds?.All(string.IsNullOrWhiteSpace) == true)
        {
            return "targetTenants.tenantIds is empty; leave it out to sync into every child tenant";
        }

        if (!string.IsNullOrWhiteSpace(c.AssociationUpdatesPath))
        {
            return "a sync with targetTenants writes no association updates (associationUpdatesPath)";
        }

        if (c.OnDuplicateKey == DuplicateKeyHandling.Report)
        {
            return "onDuplicateKey: Report is not supported with targetTenants";
        }

        return null;
    }

    /// <summary>
    /// One target: reads the existing copies in a transaction of the proven identity, plans the upserts and deletes,
    /// judges the plan with the target's write guards and writes it in the same transaction (not in a dry run). A
    /// WriteConflict is retried like the single-tenant write.
    /// </summary>
    private async Task<IReadOnlyList<EntityUpdateInfo<RtEntity>>> SyncIntoTargetAsync(ICrossTenantAccess access,
        IReadOnlyList<SyncItem> items, string sourceTenantId, DateTime syncedAt, bool isDryRun,
        INodeContext nodeContext)
    {
        await ApplySemaphoreSlim.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                IOctoSession? session = null;
                try
                {
                    // The sync writer of the pipeline's tenant: the target's engine lets it write only the copies of
                    // that source (and refuses every other session that touches them).
                    session = await access.OpenSyncWriterSessionAsync(sourceTenantId);
                    session.StartTransaction();

                    var plan = await PlanSyncAsync(access, session, items, sourceTenantId, syncedAt,
                        nodeContext.NodePath.ToString());
                    await RtAttributeAccessGuard.EnsureWritesAllowedAsync(
                        WriteGuardScope.CrossTenant(etlContext, access), ckCacheService, plan, []);

                    if (isDryRun || plan.Count == 0)
                    {
                        await AbortQuietlyAsync(session, nodeContext);
                        return plan;
                    }

                    var operationResult = new OperationResult();
                    await SyncOriginRefusal.RunAsync(nodeContext.NodePath.ToString(), () =>
                        access.Repository.ApplyChangesAsync(session, plan, [], operationResult));
                    if (operationResult.HasErrors || operationResult.HasFatalErrors)
                    {
                        await AbortQuietlyAsync(session, nodeContext);
                        nodeContext.Error("The sync into tenant '{0}' was refused by its validation: {1}",
                            access.TenantId, operationResult.GetMessages());
                        throw new CrossTenantSyncTargetRejectedException();
                    }

                    await session.CommitTransactionAsync();
                    return plan;
                }
                catch (Exception e) when (IsWriteConflict(e) && attempt < MaxWriteAttempts)
                {
                    await AbortQuietlyAsync(session, nodeContext);
                    session?.Dispose();
                    session = null;
                    var delay = WriteConflictBackoff(attempt, Random.Shared);
                    nodeContext.Debug(
                        $"Write conflict in the sync into '{access.TenantId}' (attempt {attempt} of {MaxWriteAttempts}), retrying in {delay.TotalMilliseconds:F0} ms");
                    ApplySemaphoreSlim.Release();
                    try
                    {
                        await Task.Delay(delay);
                    }
                    finally
                    {
                        await ApplySemaphoreSlim.WaitAsync();
                    }
                }
                catch (Exception) when (session != null)
                {
                    await AbortQuietlyAsync(session, nodeContext);
                    throw;
                }
                finally
                {
                    session?.Dispose();
                }
            }
        }
        finally
        {
            ApplySemaphoreSlim.Release();
        }
    }

    /// <summary>
    /// The writes of one target, keyed by source tenant and source RtId (the copy keeps the source RtId): an upsert
    /// inserts a missing copy or updates the existing one and stamps <c>RtSyncOrigin</c>; a replace of the source
    /// replaces the existing copy as a whole (an attribute the replace leaves out is gone from the copy too), with the
    /// origin stamped again; a delete removes the copy and is a no-op when there is none. An entity with that RtId that is not this sync's copy (no origin, or the origin of
    /// another source) refuses the whole target: the sync never touches a local entity.
    /// </summary>
    internal static async Task<List<EntityUpdateInfo<RtEntity>>> PlanSyncAsync(ICrossTenantAccess access,
        IOctoSession session, IReadOnlyList<SyncItem> items, string sourceTenantId, DateTime syncedAt,
        string nodePath)
    {
        var existing = new Dictionary<OctoObjectId, RtEntity>();
        foreach (var group in items.GroupBy(i => i.CkTypeId))
        {
            var ids = group.Select(i => i.RtId).Distinct().ToList();
            var found = await access.Repository.GetRtEntitiesByIdAsync(session, group.Key, ids,
                RtEntityQueryOptions.Create(), null, null);
            foreach (var entity in found.Items)
            {
                existing[entity.RtId] = entity;
            }
        }

        var plan = new List<EntityUpdateInfo<RtEntity>>();
        foreach (var item in items)
        {
            var entityId = new RtEntityId(item.CkTypeId, item.RtId);
            existing.TryGetValue(item.RtId, out var current);
            if (current != null && !SyncOrigin.IsCopyOf(SyncOrigin.Read(current), sourceTenantId, item.RtId))
            {
                throw SyncOriginWriteRefusedException.NotOwnedBySync(nodePath, access.TenantId, entityId);
            }

            if (item.IsDelete)
            {
                if (current != null)
                {
                    plan.Add(EntityUpdateInfo<RtEntity>.CreateDelete(entityId));
                }

                continue;
            }

            var attributes = SyncOrigin.WithoutOrigin(item.Attributes!);
            attributes[SyncOrigin.AttributeName] = SyncOrigin.Create(sourceTenantId, item.RtId, syncedAt);
            var copy = new RtEntity(item.CkTypeId, item.RtId, attributes) { RtWellKnownName = item.RtWellKnownName };
            plan.Add(current == null
                ? EntityUpdateInfo<RtEntity>.CreateInsert(copy)
                : item.IsReplace
                    ? EntityUpdateInfo<RtEntity>.CreateReplace(entityId, copy)
                    : EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, copy));
        }

        return plan;
    }

    private static string FailureCode(Exception ex) => ex switch
    {
        CrossTenantAccessDeniedException denied => denied.ErrorCode,
        SyncOriginWriteRefusedException owned => owned.ErrorCode,
        RtAttributeWriteRestrictedException => "ATTRIBUTE_WRITE_RESTRICTED",
        CrossTenantSyncTargetRejectedException => ValidationFailedCode,
        _ when IsWriteConflict(ex) => "WRITE_CONFLICT",
        _ => "WRITE_FAILED"
    };

    /// <summary>The target's validation refused the planned batch; details are in the pipeline log.</summary>
    private sealed class CrossTenantSyncTargetRejectedException() : Exception("The target refused the sync batch");

    /// <summary>
    /// One entity of a sync batch: the source entity (insert or update; <see cref="IsReplace" /> for a replace, which
    /// replaces the copy as a whole) or its deletion.
    /// </summary>
    internal sealed record SyncItem(RtCkId<CkTypeId> CkTypeId, OctoObjectId RtId, bool IsDelete, bool IsReplace,
        IReadOnlyDictionary<string, object?>? Attributes, string? RtWellKnownName)
    {
        /// <summary>
        /// The items of the batch, one per entity: several changes of an entity (a change stream can carry several)
        /// are applied in order (<see cref="Then" />), so no change is lost. Record array item updates and secret
        /// clears are not part of a sync (the source entity is copied as it is).
        /// </summary>
        public static IReadOnlyList<SyncItem> From(IEnumerable<EntityUpdateInfo<RtEntity>> updates, string nodePath)
        {
            var items = new List<SyncItem>();
            foreach (var update in updates)
            {
                if (update.RecordArrayItemUpdates is { Count: > 0 } || update.ClearSecretAttributes is { Count: > 0 })
                {
                    throw CrossTenantAccessDeniedException.InvalidConfiguration(nodePath,
                        "a sync copies whole entities; record array item updates and secret clears are not supported");
                }

                var isDelete = update.ModOption == EntityModOptions.Delete;
                var rtId = update.RtId ?? update.RtEntity?.RtId;
                if (rtId == null || rtId == OctoObjectId.Empty || (!isDelete && update.RtEntity == null))
                {
                    throw CrossTenantAccessDeniedException.InvalidConfiguration(nodePath,
                        "every entity update of a sync needs the source RtId (and the entity for an upsert)");
                }

                var item = new SyncItem(update.CkTypeId, rtId.Value, isDelete,
                    update.ModOption == EntityModOptions.Replace, update.RtEntity?.Attributes,
                    update.RtEntity?.RtWellKnownName);
                var index = items.FindIndex(i => i.RtId == rtId.Value && i.CkTypeId == update.CkTypeId);
                if (index < 0)
                {
                    items.Add(item);
                }
                else
                {
                    items[index] = items[index].Then(item);
                }
            }

            return items;
        }

        /// <summary>
        /// This change followed by <paramref name="next" /> of the same entity: a delete or a replace ends what came
        /// before; after a delete the entity was created again, so the next upsert replaces the copy; otherwise the
        /// next update is merged onto this one (its attributes win, the others stay).
        /// </summary>
        internal SyncItem Then(SyncItem next)
        {
            if (next.IsDelete || next.IsReplace)
            {
                return next;
            }

            if (IsDelete)
            {
                return next with { IsReplace = true };
            }

            var attributes = new Dictionary<string, object?>(Attributes ?? new Dictionary<string, object?>());
            foreach (var (name, value) in next.Attributes ?? new Dictionary<string, object?>())
            {
                attributes[name] = value;
            }

            return this with { Attributes = attributes, RtWellKnownName = next.RtWellKnownName ?? RtWellKnownName };
        }
    }

    /// <summary>
    /// AB#9769: every end of an association change in a cross-tenant write is an entity this batch inserts or an
    /// entity the target holds, read with the session the guard proved there. An end the target does not hold (for
    /// example an RtId of the pipeline's own tenant, abuse case "cross-tenant association") refuses the whole batch
    /// with CROSS_TENANT_FORBIDDEN before anything is written. Applies to creates and deletes alike.
    /// </summary>
    private static async Task EnsureAssociationEndsInTargetAsync(ICrossTenantAccess target,
        TenantTargetConfiguration targetConfiguration, IReadOnlyCollection<EntityUpdateInfo<RtEntity>> entityUpdates,
        IReadOnlyCollection<AssociationUpdateInfo> associationUpdates, INodeContext nodeContext)
    {
        if (associationUpdates.Count == 0)
        {
            return;
        }

        // An insert carries its id on the entity (the update info has none of its own for a new entity).
        var inserted = entityUpdates.Where(u => u.ModOption == EntityModOptions.Insert && u.RtEntity != null)
            .Select(u => u.RtEntity!.RtId).ToHashSet();
        var ends = associationUpdates.SelectMany(a => new[] { a.Origin, a.Target })
            .Where(end => !inserted.Contains(end.RtId)).Distinct().ToList();
        if (ends.Count == 0)
        {
            return;
        }

        using var session = await target.OpenSessionAsync();
        session.StartTransaction();
        foreach (var group in ends.GroupBy(end => end.CkTypeId))
        {
            var ids = group.Select(end => end.RtId).Distinct().ToList();
            var found = await target.Repository.GetRtEntitiesByIdAsync(session, group.Key, ids,
                RtEntityQueryOptions.Create(), null, null);
            var foundIds = found.Items.Select(e => e.RtId).ToHashSet();
            if (ids.Any(id => !foundIds.Contains(id)))
            {
                await session.AbortTransactionAsync();
                throw CrossTenantAccessDeniedException.Forbidden(nodeContext.NodePath.ToString(),
                    targetConfiguration.Relation?.ToString() ?? "(none)", CrossTenantRefusalReason.CrossTenantAssociation);
            }
        }

        await session.CommitTransactionAsync();
    }

    /// <summary>
    /// Whether a WriteConflict (112) sits anywhere in the inner-exception chain (AB#9466). The repository
    /// wraps the driver failure in an OperationFailedException, so matching only the outermost exception
    /// misses it, and the pipeline failed instead of retrying or reporting the conflict.
    /// </summary>
    internal static bool IsWriteConflict(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case MongoCommandException { Code: WriteConflictCode }:
                case MongoWriteException { WriteError.Code: WriteConflictCode }:
                case MongoBulkWriteException bulk when bulk.WriteErrors.Any(w => w.Code == WriteConflictCode):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The wait before the next attempt after a WriteConflict: an exponential ceiling (25, 50, 100, 200,
    /// 400 ms) of which a random half to all is used, so two replicas that conflicted once do not retry in
    /// lock step. Over all attempts the node waits about 0.4 to 0.8 seconds before it gives up.
    /// </summary>
    internal static TimeSpan WriteConflictBackoff(int attempt, Random random)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var ceiling = Math.Min(WriteConflictBackoffBaseMs * (1 << exponent), WriteConflictBackoffCapMs);
        return TimeSpan.FromMilliseconds(ceiling / 2.0 + random.NextDouble() * ceiling / 2.0);
    }

    /// <summary>
    /// Rolls the transaction back after a refused write. The server may already have aborted it (a
    /// WriteConflict does), and a failing abort must not hide the refusal the node is handling.
    /// </summary>
    private static async Task AbortQuietlyAsync(IOctoSession? session, INodeContext nodeContext)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            await session.AbortTransactionAsync();
        }
        catch (Exception abortFailure)
        {
            nodeContext.Debug($"Aborting the refused transaction failed: {abortFailure.GetType().Name}");
        }
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

        // Merge all attributes — later updates win on conflicts. AB#5538: the Secret clear lists
        // (ClearSecretAttributes) merge the same way: a later non-empty value withdraws an earlier
        // clear, a later clear drops an earlier value, and an "unchanged" value (null, "", marker)
        // after a clear leaves the clear in place - so the engine never receives the contradictory
        // "set and cleared" pair (rule message 23) for a merge it did not ask for.
        var mergedAttributes = new Dictionary<string, object?>();
        var clearSecretAttributes = new List<string>();
        // AB#7436: genuine conflicts are collected per attribute (first conflicting pair) and reported
        // in ONE warning per entity and batch.
        var conflicts = new Dictionary<string, (object? Existing, object? Incoming)>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (update.RtEntity != null)
            {
                foreach (var kvp in update.RtEntity.Attributes)
                {
                    if (clearSecretAttributes.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (kvp.Value is not string { Length: > 0 })
                        {
                            // null, "" or a {"isSet": …} marker after a clear: the clear stands.
                            continue;
                        }

                        clearSecretAttributes.RemoveAll(c =>
                            string.Equals(c, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    }

                    if (mergedAttributes.TryGetValue(kvp.Key, out var existing) &&
                        !AttributeValueComparer.AreEqual(existing, kvp.Value))
                    {
                        conflicts.TryAdd(kvp.Key, (existing, kvp.Value));
                    }

                    mergedAttributes[kvp.Key] = kvp.Value;
                }
            }

            foreach (var cleared in update.ClearSecretAttributes ?? [])
            {
                foreach (var key in mergedAttributes.Keys
                             .Where(k => string.Equals(k, cleared, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    mergedAttributes.Remove(key);
                }

                if (!clearSecretAttributes.Contains(cleared, StringComparer.OrdinalIgnoreCase))
                {
                    clearSecretAttributes.Add(cleared);
                }
            }
        }

        if (conflicts.Count > 0)
        {
            var details = string.Join(", ", conflicts.Select(c =>
                $"'{c.Key}' ({AttributeValueComparer.FormatAttribute(c.Key, c.Value.Existing)} vs " +
                $"{AttributeValueComparer.FormatAttribute(c.Key, c.Value.Incoming)})"));
            // The values go in as an argument: they may contain braces, which a log template would parse.
            nodeContext.Warning(
                conflicts.Count == 1
                    ? "Merging entity {RtEntityId}: attribute {Conflicts} has conflicting values — later value wins"
                    : "Merging entity {RtEntityId}: attributes {Conflicts} have conflicting values — later value wins",
                last.GetRtEntityId().ToString()!, details);
        }

        var mergedEntity = new RtEntity(lastEntity.CkTypeId!, last.GetRtEntityId().RtId, mergedAttributes)
        {
            RtChangedDateTime = lastEntity.RtChangedDateTime,
            RtWellKnownName = lastEntity.RtWellKnownName
        };

        // AB#7442: atomic RecordArray item updates of all items are kept in order (the engine merges entries for the
        // same item, the last value wins).
        var itemUpdates = updates.SelectMany(u => u.RecordArrayItemUpdates ?? []).ToList();
        if (itemUpdates.Count > 0)
        {
            return EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(last.GetRtEntityId(), mergedEntity,
                itemUpdates, clearSecretAttributes.Count > 0 ? clearSecretAttributes : null);
        }

        return EntityUpdateInfo<RtEntity>.CreateUpdate(last.GetRtEntityId(), mergedEntity,
            clearSecretAttributes.Count > 0 ? clearSecretAttributes : null);
    }
}
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal sealed record NotificationDeliveryStateChange(
    DateTime? ProcessedUntil = null,
    IReadOnlyList<SentRow>? SentRows = null,
    string? QueryRtId = null,
    string? SubjectId = null,
    bool? SkipExistingRows = null,
    DateTime? LastRunAt = null,
    DateTime? LastSentAt = null,
    string? LastError = null,
    DateTime? RunningSince = null,
    bool ClearLastError = false,
    bool ClearLastSentAt = false);

internal static class NotificationDeliveryStateStore
{
    public static async Task<TTarget?> FirstTargetAsync<TOrigin, TTarget>(ITenantRepository repository,
        IOctoSession session, OctoObjectId originRtId, RtCkId<CkAssociationRoleId> roleId, GraphDirections direction)
        where TOrigin : RtEntity, new()
        where TTarget : RtEntity, new() =>
        (await TargetsAsync<TOrigin, TTarget>(repository, session, originRtId, roleId, direction)).FirstOrDefault();

    public static async Task<IReadOnlyList<TTarget>> TargetsAsync<TOrigin, TTarget>(ITenantRepository repository,
        IOctoSession session, OctoObjectId originRtId, RtCkId<CkAssociationRoleId> roleId, GraphDirections direction)
        where TOrigin : RtEntity, new()
        where TTarget : RtEntity, new()
    {
        var targets = await repository.GetRtAssociationTargetsAsync<TOrigin, TTarget>(session, [originRtId], roleId,
            direction, null, RtEntityQueryOptions.Create());
        return targets.Values.SelectMany(resultSet => resultSet.Items).ToList();
    }

    public static Task<RtNotificationDeliveryState?> LoadAsync(ITenantRepository repository, IOctoSession session,
        OctoObjectId preferenceRtId) =>
        FirstTargetAsync<RtNotificationPreference, RtNotificationDeliveryState>(repository, session, preferenceRtId,
            SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Inbound);

    public static DeliveryMemory? MemoryOf(RtNotificationDeliveryState? state) =>
        state == null
            ? null
            : new DeliveryMemory(state.ProcessedUntil,
                state.SentRows?.Where(row => !string.IsNullOrEmpty(row.RowRtId))
                    .Select(row => new SentRow(row.RowRtId!, row.SentAt, row.ChangedAt)).ToList() ?? [],
                state.QueryRtId, state.SubjectId, state.SkipExistingRows, state.LastRunAt);

    public static OctoObjectId StateRtIdFor(OctoObjectId preferenceRtId)
    {
        var bytes = preferenceRtId.ToByteArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0x5A;
        }

        return new OctoObjectId(bytes);
    }

    public static async Task<OctoObjectId?> TryAcquireLeaseAsync(IMeshEtlContext etlContext, OctoObjectId? stateRtId,
        OctoObjectId preferenceRtId, DateTime now, TimeSpan leaseExpiry)
    {
        var lease = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        try
        {
            if (stateRtId == null)
            {
                stateRtId = await WriteAsync(etlContext, null, preferenceRtId,
                    new NotificationDeliveryStateChange(RunningSince: lease), null);
            }
            else
            {
                await WriteAsync(etlContext, stateRtId, preferenceRtId,
                    new NotificationDeliveryStateChange(RunningSince: lease),
                    new AttributeNewerThanGuard("attributes.runningSince", lease - leaseExpiry));
            }
        }
        catch (Exception)
        {
            if ((await ReadStateAsync(etlContext, preferenceRtId))?.RunningSince is { } held && held != lease)
            {
                return null;
            }

            throw;
        }

        return (await ReadStateAsync(etlContext, preferenceRtId))?.RunningSince == lease ? stateRtId : null;
    }

    public static async Task<OctoObjectId> SaveAsync(IMeshEtlContext etlContext, OctoObjectId? stateRtId,
        OctoObjectId preferenceRtId, NotificationDeliveryStateChange change)
    {
        if (stateRtId != null)
        {
            return await WriteAsync(etlContext, stateRtId, preferenceRtId, change, null);
        }

        try
        {
            return await WriteAsync(etlContext, null, preferenceRtId, change, null);
        }
        catch (Exception)
        {
            if ((await ReadStateAsync(etlContext, preferenceRtId))?.RtId is not { } created)
            {
                throw;
            }

            return await WriteAsync(etlContext, created, preferenceRtId, change, null);
        }
    }

    private static async Task<RtNotificationDeliveryState?> ReadStateAsync(IMeshEtlContext etlContext,
        OctoObjectId preferenceRtId)
    {
        // AB#5028 — SYSTEM, as the write below: this re-read decides who holds the lease. Under a read
        // filter another run's state would read as missing and both runs would send.
        using var session = await etlContext.GetSystemSessionAsync();
        session.StartTransaction();
        var state = await LoadAsync(etlContext.TenantRepository, session, preferenceRtId);
        await session.CommitTransactionAsync();
        return state;
    }

    private static async Task<OctoObjectId> WriteAsync(IMeshEtlContext etlContext, OctoObjectId? stateRtId,
        OctoObjectId preferenceRtId, NotificationDeliveryStateChange change, AttributeNewerThanGuard? guard)
    {
        var state = new RtNotificationDeliveryState { RunningSince = change.RunningSince };
        if (change.ProcessedUntil != null)
        {
            state.ProcessedUntil = change.ProcessedUntil;
        }

        if (change.SentRows != null)
        {
            var sentRows = new AttributeRecordValueList<RtSentRowRecord>();
            foreach (var row in change.SentRows)
            {
                sentRows.Add(new RtSentRowRecord { RowRtId = row.RtId, SentAt = row.SentAt, ChangedAt = row.ChangedAt });
            }

            state.SentRows = sentRows;
        }

        if (change.QueryRtId != null)
        {
            state.QueryRtId = change.QueryRtId;
        }

        if (change.SubjectId != null)
        {
            state.SubjectId = change.SubjectId;
        }

        if (change.SkipExistingRows != null)
        {
            state.SkipExistingRows = change.SkipExistingRows;
        }

        if (change.LastRunAt != null)
        {
            state.LastRunAt = change.LastRunAt;
        }

        if (change.LastSentAt != null || change.ClearLastSentAt)
        {
            state.LastSentAt = change.LastSentAt;
        }

        if (change.LastError != null || change.ClearLastError)
        {
            state.LastError = change.LastError;
        }

        var operationResult = new OperationResult();
        // AB#5028 — SYSTEM by decision: the delivery state is the engine's own bookkeeping, not the
        // recipient's data. Under a read filter a state the identity may not see reads as missing,
        // and the next run would insert a second one and mail everything again.
        using var session = await etlContext.GetSystemSessionAsync();
        session.StartTransaction();
        if (stateRtId == null)
        {
            state.RtId = StateRtIdFor(preferenceRtId);
            await etlContext.TenantRepository.ApplyChangesAsync(session,
                [EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state)],
                [
                    AssociationUpdateInfo.CreateInsert(
                        new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state.RtId),
                        new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preferenceRtId),
                        SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId)
                ], operationResult);
        }
        else
        {
            var stateId = new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, stateRtId.Value);
            await etlContext.TenantRepository.ApplyChangesAsync(session,
                [
                    guard == null
                        ? EntityUpdateInfo<RtEntity>.CreateUpdate(stateId, state)
                        : EntityUpdateInfo<RtEntity>.CreateConditionalUpdate(stateId, state, guard)
                ], [], operationResult);
        }

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw MeshAdapterPipelineExecutionException.RepositoryUpdateOperationFailed(operationResult);
        }

        await session.CommitTransactionAsync();
        return stateRtId ?? state.RtId;
    }
}

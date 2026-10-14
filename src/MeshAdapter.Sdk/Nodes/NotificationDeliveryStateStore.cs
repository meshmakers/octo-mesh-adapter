using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal sealed record NotificationDeliveryStateChange(
    IReadOnlyCollection<string>? SeenRtIds = null,
    IReadOnlyCollection<string>? SkippedRtIds = null,
    string? QueryRtId = null,
    DateTime? LastRunAt = null,
    DateTime? LastSentAt = null,
    string? LastError = null,
    DateTime? RunningSince = null,
    bool ClearLastError = false);

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

    public static async Task<OctoObjectId> SaveAsync(IMeshEtlContext etlContext, OctoObjectId? stateRtId,
        OctoObjectId preferenceRtId, NotificationDeliveryStateChange change)
    {
        var state = new RtNotificationDeliveryState { RunningSince = change.RunningSince };
        if (change.SeenRtIds != null)
        {
            state.SeenRtIds = new AttributeStringValueList(change.SeenRtIds.ToList());
        }

        if (change.SkippedRtIds != null)
        {
            state.SkippedRtIds = new AttributeStringValueList(change.SkippedRtIds.ToList());
        }

        if (change.QueryRtId != null)
        {
            state.QueryRtId = change.QueryRtId;
        }

        if (change.LastRunAt != null)
        {
            state.LastRunAt = change.LastRunAt;
        }

        if (change.LastSentAt != null)
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
            state.RtId = OctoObjectId.GenerateNewId();
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
            await etlContext.TenantRepository.ApplyChangesAsync(session,
                [
                    EntityUpdateInfo<RtEntity>.CreateUpdate(
                        new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, stateRtId.Value), state)
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

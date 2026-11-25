using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>Stable error code of a write the sync-origin rules refused (AB#9770). Safe to match on in a client.</summary>
public static class SyncOriginCodes
{
    /// <summary>The write would change or delete synced master data, or set or change its origin.</summary>
    public const string SyncOwnedEntity = "SYNC_OWNED_ENTITY";
}

/// <summary>
///     A write refused because it touches master data that a cross-tenant sync owns (AB#9770): an entity whose
///     <c>RtSyncOrigin</c> names its source tenant, or an update that sets or changes <c>RtSyncOrigin</c> itself.
/// </summary>
public sealed class SyncOriginWriteRefusedException : PipelineExecutionException
{
    private SyncOriginWriteRefusedException(string message) : base(message)
    {
    }

    /// <summary>The stable error code, <see cref="SyncOriginCodes.SyncOwnedEntity" />.</summary>
    public string ErrorCode => SyncOriginCodes.SyncOwnedEntity;

    internal static SyncOriginWriteRefusedException OriginWritten(string nodePath, string ckTypeId) =>
        new($"[{nodePath}]: {SyncOriginCodes.SyncOwnedEntity}: an update of '{ckTypeId}' writes " +
            $"{SyncOrigin.AttributeName}; only the master-data sync (ApplyChanges@2 with targetTenants) sets it.");

    internal static SyncOriginWriteRefusedException SyncOwned(string nodePath, RtEntityId entityId,
        string? sourceTenantId) =>
        new($"[{nodePath}]: {SyncOriginCodes.SyncOwnedEntity}: entity '{entityId}' is master data synced from tenant " +
            $"'{sourceTenantId ?? "(unknown)"}'; change or delete it there, the sync brings the change here.");

    internal static SyncOriginWriteRefusedException NotOwnedBySync(string nodePath, string targetTenantId,
        RtEntityId entityId) =>
        new($"[{nodePath}]: {SyncOriginCodes.SyncOwnedEntity}: entity '{entityId}' in tenant '{targetTenantId}' is not " +
            "a copy of this sync (no or another origin); the sync never changes or deletes it.");
}

/// <summary>
///     The owner marker of synced master data (AB#9770): the System record <c>RtSyncOrigin</c> (source tenant, source
///     RtId as text, sync time) on <c>System/Entity</c>.
/// </summary>
internal static class SyncOrigin
{
    /// <summary>The attribute name of the origin on every entity.</summary>
    public const string AttributeName = "RtSyncOrigin";

    /// <summary>Source tenant and source RtId of a stored entity, or <c>null</c> when it carries no origin.</summary>
    public static (string? SourceTenantId, string? SourceRtId)? Read(RtEntity entity)
    {
        var value = Find(entity.Attributes, out _);
        return value switch
        {
            RtRecord record => (new RtSyncOriginRecord(record).SourceTenantId,
                new RtSyncOriginRecord(record).SourceRtId),
            // Present but not a record (it should not exist): treat it as an origin of nobody, so nothing local edits it.
            not null => (null, null),
            null => null
        };
    }

    /// <summary>Whether <paramref name="origin" /> is exactly the copy of (<paramref name="sourceTenantId" />, <paramref name="sourceRtId" />).</summary>
    public static bool IsCopyOf((string? SourceTenantId, string? SourceRtId)? origin, string sourceTenantId,
        OctoObjectId sourceRtId) =>
        origin is { } o &&
        string.Equals(o.SourceTenantId?.Trim(), sourceTenantId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(o.SourceRtId?.Trim(), sourceRtId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an update writes the origin (a value, a null, an array item update or a secret clear of it).</summary>
    public static bool IsWrittenBy(IEntityUpdateInfo<RtEntity> update)
    {
        if (update.ModOption == EntityModOptions.Delete)
        {
            return false;
        }

        if (update.RtEntity != null)
        {
            Find(update.RtEntity.Attributes, out var present);
            if (present)
            {
                return true;
            }
        }

        return (update.RecordArrayItemUpdates ?? []).Any(i => IsName(i.AttributeName)) ||
               (update.ClearSecretAttributes ?? []).Any(IsName);
    }

    /// <summary>The attributes of a source entity without any origin it carries (a source can itself be a copy).</summary>
    public static Dictionary<string, object?> WithoutOrigin(IReadOnlyDictionary<string, object?> attributes) =>
        attributes.Where(a => !IsName(a.Key)).ToDictionary(a => a.Key, a => a.Value);

    /// <summary>
    ///     The origin record the sync stamps on a copy. Updating an entity with a generated record class needs
    ///     octo-construction-kit-engine-mongodb with the record normalisation of the update path (AB#9770); before it,
    ///     the update of an existing copy failed with "not configured as a type that is allowed to be serialized".
    /// </summary>
    public static RtSyncOriginRecord Create(string sourceTenantId, OctoObjectId sourceRtId, DateTime syncedAtUtc) =>
        new()
        {
            SourceTenantId = sourceTenantId,
            SourceRtId = sourceRtId.ToString(),
            SyncedAt = syncedAtUtc
        };

    private static bool IsName(string? name) =>
        string.Equals(name, AttributeName, StringComparison.OrdinalIgnoreCase);

    private static object? Find(IReadOnlyDictionary<string, object?> attributes, out bool present)
    {
        foreach (var (key, value) in attributes)
        {
            if (IsName(key))
            {
                present = true;
                return value;
            }
        }

        present = false;
        return null;
    }
}

/// <summary>
///     The write rules of synced master data (AB#9770) for every write except the sync itself (ApplyChanges@1,
///     ApplyChanges@2 into the own tenant or one target tenant).
/// </summary>
/// <remarks>
///     Invariant: an entity that carries <c>RtSyncOrigin</c> is changed and deleted only by the sync of its source
///     tenant, and <c>RtSyncOrigin</c> is set or changed only by that sync. So a company can neither edit the group's
///     master data locally (the next sync would silently overwrite it) nor forge an origin (claiming an entity of its own
///     for the group, or pinning one of its entities against its own edits).
/// </remarks>
internal static class SyncOriginWriteGuard
{
    /// <summary>
    ///     Rule 1, no database read: no update of the batch writes <c>RtSyncOrigin</c>. Call it before the dry-run branch.
    /// </summary>
    public static void EnsureOriginNotWritten(string nodePath, IEnumerable<IEntityUpdateInfo<RtEntity>> updates)
    {
        foreach (var update in updates)
        {
            if (SyncOrigin.IsWrittenBy(update))
            {
                throw SyncOriginWriteRefusedException.OriginWritten(nodePath, update.CkTypeId.FullName);
            }
        }
    }

    /// <summary>
    ///     Rule 2, inside the write transaction: no update, replace or delete of the batch targets a stored entity that
    ///     carries <c>RtSyncOrigin</c>. Reads only the entities of the batch that carry an origin (one query per type).
    /// </summary>
    public static async Task EnsureNoSyncOwnedEntityAsync(ITenantRepository repository, IOctoSession session,
        string nodePath, IEnumerable<IEntityUpdateInfo<RtEntity>> updates)
    {
        var byType = updates.Where(u => u.ModOption != EntityModOptions.Insert && u.RtId != null)
            .GroupBy(u => u.CkTypeId);
        foreach (var group in byType)
        {
            var ids = group.Select(u => u.RtId!.Value).Distinct().ToList();
            var options = RtEntityQueryOptions.Create();
            options.Field(SyncOrigin.AttributeName, FieldFilterOperator.IsNotNull, null);
            var owned = await repository.GetRtEntitiesByIdAsync(session, group.Key, ids, options, null, null);
            var first = owned.Items.FirstOrDefault(e => SyncOrigin.Read(e) != null);
            if (first != null)
            {
                throw SyncOriginWriteRefusedException.SyncOwned(nodePath, first.ToRtEntityId(),
                    SyncOrigin.Read(first)?.SourceTenantId);
            }
        }
    }
}

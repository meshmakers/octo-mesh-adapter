using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
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

    private SyncOriginWriteRefusedException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>The stable error code, <see cref="SyncOriginCodes.SyncOwnedEntity" />.</summary>
    public string ErrorCode => SyncOriginCodes.SyncOwnedEntity;

    /// <summary>
    ///     The engine's refusal (<see cref="SyncOriginWriteRules" />, raised in the write funnel of the target tenant) as
    ///     the node's error: same stable code, the engine's message names the entity and the reason.
    /// </summary>
    internal static SyncOriginWriteRefusedException FromEngine(string nodePath, RuntimeRepositoryException refusal)
    {
        var message = refusal.OperationResult.Messages.FirstOrDefault(m =>
            m.MessageNumber == SyncOriginWriteRules.MessageNumber &&
            m.Parameters.TryGetValue("code", out var code) && code == SyncOriginWriteRules.ErrorCode);
        return new SyncOriginWriteRefusedException(
            $"[{nodePath}]: {SyncOriginCodes.SyncOwnedEntity}: {message?.MessageText ?? refusal.Message}", refusal);
    }

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
///     The write rule of synced master data lives in the engine (<see cref="SyncOriginWriteRules" />): every tenant
///     repository refuses, in its write funnel, an update, replace or delete of an entity carrying <c>RtSyncOrigin</c> and
///     every write of <c>RtSyncOrigin</c>, except by a sync-writer session for the copies of its own source tenant. The
///     write nodes only map that refusal to their stable error (<see cref="SyncOriginCodes.SyncOwnedEntity" />).
/// </summary>
internal static class SyncOriginRefusal
{
    /// <summary>Runs <paramref name="write" />; the engine's sync-origin refusal becomes <see cref="SyncOriginWriteRefusedException" />.</summary>
    public static async Task RunAsync(string nodePath, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (RuntimeRepositoryException e) when (SyncOriginWriteRules.IsRefusal(e))
        {
            throw SyncOriginWriteRefusedException.FromEngine(nodePath, e);
        }
    }
}

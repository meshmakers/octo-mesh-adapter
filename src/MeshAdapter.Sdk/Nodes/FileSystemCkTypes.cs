using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
///     AB#6177 — type resolution of the file nodes (<c>CreateFileSystemUpdate@1</c>,
///     <c>GetFileSystemContent@1</c>, <c>CreateZipArchive@1</c>, <c>ToDiscord@1</c>) during the move of
///     the platform file system from <c>System.Reporting</c> to <c>System.Files</c> (AB#6171).
/// </summary>
/// <remarks>
///     <para>
///         <b>New type first, old type only as fallback</b> (concept §8.6). The resolution is driven by
///         where the data is, not by which CK models a tenant has imported: a tenant that already
///         imported <c>System.Files</c> but has not been migrated yet still owns its roots (e.g.
///         <c>Documents</c>) as <c>System.Reporting/FolderRoot</c>, so a root is looked up in
///         <c>System.Files</c> first and, if absent there, in <c>System.Reporting</c>; items are then
///         created with the type family of the root that was found. An item looked up by RtId is
///         searched in <c>System.Files</c> first and then in <c>System.Reporting</c>.
///     </para>
///     <para>
///         A lookup against a type the tenant does not know (model not imported, or already dropped)
///         counts as "not found there" (only a <see cref="CkCacheException" />; every other failure propagates) and never fails the node on its own; the tenant CK cache
///         reloads itself on such a miss (AB#4444 / AB#5415), which is what lets a stale adapter
///         find the new type.
///     </para>
///     <para>
///         The helpers take the caller's session: which identity reads (system or scoped) stays an
///         explicit decision at the node's call site (AB#5028).
///     </para>
/// </remarks>
internal static class FileSystemCkTypes
{
    internal static readonly RtCkId<CkTypeId> FileSystemItem = new("System.Files/FileSystemItem");
    internal static readonly RtCkId<CkTypeId> FolderRoot = new("System.Files/FolderRoot");
    internal static readonly RtCkId<CkTypeId> LegacyFileSystemItem = new("System.Reporting/FileSystemItem");
    internal static readonly RtCkId<CkTypeId> LegacyFolderRoot = new("System.Reporting/FolderRoot");

    /// <summary>A resolved folder root together with the file type that belongs under it.</summary>
    internal sealed record ResolvedRoot(RtEntity Root, RtCkId<CkTypeId> ItemCkTypeId);

    /// <summary>
    ///     Finds the folder root with the given well-known name: <c>System.Files</c> first, then
    ///     <c>System.Reporting</c>. Returns <c>null</c> when neither has exactly one match.
    /// </summary>
    internal static async Task<ResolvedRoot?> FindFolderRootAsync(ITenantRepository repository,
        IOctoSession session, string rootFolderWellKnownName)
    {
        var candidates = new[]
        {
            (RootType: FolderRoot, ItemType: FileSystemItem),
            (RootType: LegacyFolderRoot, ItemType: LegacyFileSystemItem)
        };

        CkCacheException? firstFailure = null;
        var answered = 0;
        foreach (var (rootType, itemType) in candidates)
        {
            try
            {
                var queryOptions = RtEntityQueryOptions.Create()
                    .FieldEquals(nameof(RtEntity.RtWellKnownName), rootFolderWellKnownName);
                var result = await repository.GetRtEntitiesByTypeAsync(session, rootType, queryOptions);
                answered++;
                var items = result.Items.ToList();
                if (items.Count == 1)
                {
                    return new ResolvedRoot(items[0], itemType);
                }
            }
            catch (CkCacheException ex)
            {
                // Type unknown to this tenant (model not imported / already dropped): not found here.
                // Any other failure (timeout, cancellation, database) propagates instead of falling back.
                firstFailure ??= ex;
            }
        }

        // Neither type answered at all: that is a repository problem, not a missing root.
        if (answered == 0 && firstFailure != null)
        {
            throw firstFailure;
        }

        return null;
    }

    /// <summary>
    ///     Runs <paramref name="lookup"/> for <c>System.Files/FileSystemItem</c>, then for
    ///     <c>System.Reporting/FileSystemItem</c>, and returns the first hit.
    /// </summary>
    internal static async Task<RtEntity?> FindItemAsync(Func<RtCkId<CkTypeId>, Task<RtEntity?>> lookup)
    {
        CkCacheException? newTypeFailure = null;
        CkCacheException? legacyFailure = null;
        bool newAnswered = false, legacyAnswered = false;

        try
        {
            var entity = await lookup(FileSystemItem);
            newAnswered = true;
            if (entity != null)
            {
                return entity;
            }
        }
        catch (CkCacheException ex)
        {
            newTypeFailure = ex;
        }

        try
        {
            var entity = await lookup(LegacyFileSystemItem);
            legacyAnswered = true;
            if (entity != null)
            {
                return entity;
            }
        }
        catch (CkCacheException ex)
        {
            legacyFailure = ex;
        }

        // Both failed (neither type answered): surface the problem instead of "not found".
        if (!newAnswered && !legacyAnswered)
        {
            throw newTypeFailure ?? legacyFailure!;
        }

        return null;
    }
}

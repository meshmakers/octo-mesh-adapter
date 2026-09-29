namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     Flattens a mailbox's folder tree into the list the picker shows (AB#5370): depth-first, in
///     the order the server returns the folders, capped at a maximum with a flag saying that the
///     cap cut something off. Channel-agnostic on purpose — the IMAP and Graph listings differ only
///     in how children are fetched and how a path is spelled, and both are passed in.
/// </summary>
internal static class MailFolderTree
{
    /// <summary>
    ///     One folder as the picker receives it. <see cref="Path" /> is EXACTLY the string the
    ///     channel's trigger accepts for that folder; <see cref="DisplayName" /> is the leaf name for
    ///     display, indented by <see cref="Depth" /> (0 = a root folder).
    /// </summary>
    internal sealed record Entry(string Path, string DisplayName, int Depth);

    /// <summary>The flattened tree, and whether <c>maxFolders</c> left folders out.</summary>
    internal sealed record Listing(IReadOnlyList<Entry> Folders, bool Truncated);

    /// <summary>
    ///     Walks <paramref name="roots" /> depth-first. <paramref name="childrenOf" /> fetches a
    ///     folder's children (called once per listed folder, so a folder beyond the cap is never
    ///     fetched); <paramref name="pathOf" /> receives the folder and its parent's entry (null for a
    ///     root) and returns the trigger-facing path.
    /// </summary>
    internal static async Task<Listing> WalkDepthFirstAsync<T>(
        IReadOnlyList<T> roots,
        Func<T, Task<IReadOnlyList<T>>> childrenOf,
        Func<T, string> displayNameOf,
        Func<T, Entry?, string> pathOf,
        int maxFolders)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFolders, 0);

        var entries = new List<Entry>();
        var truncated = await VisitAsync(roots, null, entries, childrenOf, displayNameOf, pathOf, maxFolders);
        return new Listing(entries, truncated);
    }

    private static async Task<bool> VisitAsync<T>(
        IReadOnlyList<T> nodes,
        Entry? parent,
        List<Entry> entries,
        Func<T, Task<IReadOnlyList<T>>> childrenOf,
        Func<T, string> displayNameOf,
        Func<T, Entry?, string> pathOf,
        int maxFolders)
    {
        foreach (var node in nodes)
        {
            // Checked BEFORE the add: "truncated" has to mean that a folder was actually left out,
            // not that the mailbox happened to have exactly maxFolders of them.
            if (entries.Count >= maxFolders)
            {
                return true;
            }

            var entry = new Entry(pathOf(node, parent), displayNameOf(node), parent is null ? 0 : parent.Depth + 1);
            entries.Add(entry);

            var children = await childrenOf(node);
            if (children.Count > 0 &&
                await VisitAsync(children, entry, entries, childrenOf, displayNameOf, pathOf, maxFolders))
            {
                return true;
            }
        }

        return false;
    }
}

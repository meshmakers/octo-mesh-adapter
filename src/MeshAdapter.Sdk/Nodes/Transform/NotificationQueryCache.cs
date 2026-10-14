using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

internal static class NotificationQueryCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    public static Task<IReadOnlyList<OctoObjectId>> GetOrRunAsync(string key, DateTime now,
        Func<Task<IReadOnlyList<OctoObjectId>>> run)
    {
        foreach (var expired in Entries.Where(entry => entry.Value.ExpiresAt <= now).ToList())
        {
            Entries.TryRemove(expired);
        }

        Entry? added = null;
        added = new Entry(now + Lifetime, new Lazy<Task<IReadOnlyList<OctoObjectId>>>(async () =>
        {
            try
            {
                return await run();
            }
            catch
            {
                Entries.TryRemove(new KeyValuePair<string, Entry>(key, added!));
                throw;
            }
        }));

        return Entries.GetOrAdd(key, added).Result.Value;
    }

    internal static void Clear() => Entries.Clear();

    private sealed record Entry(DateTime ExpiresAt, Lazy<Task<IReadOnlyList<OctoObjectId>>> Result);
}

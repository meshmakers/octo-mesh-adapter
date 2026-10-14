using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

[Collection(nameof(NotificationQueryCacheTests))]
public class NotificationQueryCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<OctoObjectId> Ids = [new OctoObjectId("000000000000000000000001")];

    public NotificationQueryCacheTests() => NotificationQueryCache.Clear();

    [Fact]
    public async Task ConcurrentTicksOfOneFiring_RunTheQueryOnce()
    {
        var runs = 0;
        var release = new TaskCompletionSource();
        Task<IReadOnlyList<OctoObjectId>> Run() => Task.Run(async () =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return Ids;
        });

        var first = NotificationQueryCache.GetOrRunAsync("k", Now, Run);
        var second = NotificationQueryCache.GetOrRunAsync("k", Now.AddSeconds(1), Run);
        release.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AfterTheLifetime_TheQueryRunsAgain()
    {
        var runs = 0;
        Task<IReadOnlyList<OctoObjectId>> Run()
        {
            runs++;
            return Task.FromResult(Ids);
        }

        await NotificationQueryCache.GetOrRunAsync("k", Now, Run);
        await NotificationQueryCache.GetOrRunAsync("k", Now + NotificationQueryCache.Lifetime, Run);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFailedQuery_IsNotKept()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationQueryCache.GetOrRunAsync("k", Now,
            () => Task.FromException<IReadOnlyList<OctoObjectId>>(new InvalidOperationException("down"))));

        var result = await NotificationQueryCache.GetOrRunAsync("k", Now.AddSeconds(1), () => Task.FromResult(Ids));

        Assert.Same(Ids, result);
    }

    [Fact]
    public async Task DifferentKeys_RunSeparately()
    {
        var runs = 0;
        Task<IReadOnlyList<OctoObjectId>> Run()
        {
            runs++;
            return Task.FromResult(Ids);
        }

        await NotificationQueryCache.GetOrRunAsync("a", Now, Run);
        await NotificationQueryCache.GetOrRunAsync("b", Now, Run);

        Assert.Equal(2, runs);
    }
}

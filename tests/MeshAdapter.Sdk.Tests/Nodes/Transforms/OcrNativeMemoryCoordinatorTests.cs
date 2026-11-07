using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// AB#8407: IronOCR calls run one at a time per process, and native memory is released after each one —
/// also when the call throws — before the next call starts.
/// </summary>
public class OcrNativeMemoryCoordinatorTests
{
    [Fact]
    public async Task RunAsync_SerialisesParallelOcrCalls_AndReleasesAfterEach()
    {
        var running = 0;
        var maxRunning = 0;
        var released = 0;
        var sut = new OcrNativeMemoryCoordinator(() => Interlocked.Increment(ref released));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => sut.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return i;
        }), TestContext.Current.CancellationToken)));

        Assert.Equal(Enumerable.Range(0, 8), results.Order());
        Assert.Equal(1, maxRunning);
        Assert.Equal(8, released);
    }

    [Fact]
    public async Task RunAsync_ReleasesAfterEachCallAndBeforeTheNextStarts()
    {
        var log = new List<string>();
        var sut = new OcrNativeMemoryCoordinator(() => log.Add("release"));

        await sut.RunAsync(() => { log.Add("ocr1"); return 1; });
        await sut.RunAsync(() => { log.Add("ocr2"); return 2; });

        Assert.Equal(["ocr1", "release", "ocr2", "release"], log);
    }

    [Fact]
    public async Task RunAsync_OcrThrows_ReleasesFreesTheGateAndRethrows()
    {
        var released = 0;
        var sut = new OcrNativeMemoryCoordinator(() => released++);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RunAsync<int>(() => throw new InvalidOperationException("corrupt page")));
        Assert.Equal(5, await sut.RunAsync(() => 5));
        Assert.Equal(2, released);
    }

    [Fact]
    public async Task RunAsync_ReleaseThrows_DoesNotChangeTheResultOrBlockTheGate()
    {
        var sut = new OcrNativeMemoryCoordinator(() => throw new InvalidOperationException("trim failed"));

        Assert.Equal(7, await sut.RunAsync(() => 7));
        Assert.Equal(8, await sut.RunAsync(() => 8));
    }

    [Fact]
    public async Task RunAsync_WhileAnotherCallRuns_ReturnsAPendingTaskInsteadOfBlockingTheCaller()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sut = new OcrNativeMemoryCoordinator(() => { });

        var first = Task.Run(() => sut.RunAsync(() => { started.Set(); release.Wait(); return 1; }),
            TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Called on this thread: must come back at once with a pending task, not block on the gate.
        var second = sut.RunAsync(() => 2);
        Assert.False(second.IsCompleted);

        release.Set();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
    }

    [Fact]
    public void TryMallocTrim_NeverThrows_AndIsANoOpOffLinux()
    {
        var trimmed = OcrNativeMemoryCoordinator.TryMallocTrim();

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(trimmed);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

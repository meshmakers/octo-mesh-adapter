using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
/// Pins the keyed token-service contracts of <see cref="ServiceAccountTokenService"/>:
/// (1) tokens are cached PER configuration name — two service accounts on one adapter
/// never receive each other's token (the pre-consolidation identity-blind cache bug);
/// (2) <c>GetAccessTokenAsync</c> never touches the adapter-global
/// <c>IServiceClientAccessToken</c> (the pre-consolidation clobbering bug);
/// (3) <c>EnsureTokenAsync</c> keeps main's implementation and its global side effect — pinned
/// in <c>ServiceAccountTokenServiceTests</c>, not here;
/// (4) expired tokens are re-acquired; failures are not negatively cached.
/// The network round-trip is substituted via the protected virtual
/// <c>AcquireTokenAsync</c> seam; the cache/side-effect logic under test is real.
/// </summary>
public class ServiceAccountTokenServiceKeyedCacheTests
{
    private sealed class StubTokenService(
        IServiceClientAccessToken globalToken,
        Func<string, ServiceAccountTokenService.CachedToken?> mint)
        : ServiceAccountTokenService(globalToken, NullLogger<ServiceAccountTokenService>.Instance, new HttpClient())
    {
        private int _acquireCount;

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        /// <summary>When set, every acquisition awaits this gate before minting (single-flight probe).</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Restricts <see cref="Gate" /> to one configuration name; null gates every name.</summary>
        public string? GatedName { get; init; }

        /// <summary>
        /// When set, the gate is awaited without the cancellation token — models the repository read,
        /// which cannot be cancelled and outlives the flight timeout.
        /// </summary>
        public bool GateIgnoresCancellation { get; init; }

        protected override async Task<CachedToken?> AcquireTokenAsync(ITenantRepository tenantRepository,
            string wellKnownName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acquireCount);
            if (Gate is not null && (GatedName is null || GatedName == wellKnownName))
            {
                if (GateIgnoresCancellation)
                {
                    await Gate.Task;
                }
                else
                {
                    await Gate.Task.WaitAsync(cancellationToken);
                }
            }

            return mint(wellKnownName);
        }
    }

    private static ServiceAccountTokenService.CachedToken ValidToken(string name) =>
        new($"token-for-{name}", DateTime.UtcNow.AddHours(1));

    private readonly ITenantRepository _repo = A.Fake<ITenantRepository>();

    [Fact]
    public async Task GetAccessTokenAsync_TwoConfigurationNames_ReturnsDistinctTokensAndCachesPerName()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, ValidToken);

        var tokenA1 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken);
        var tokenB1 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta", TestContext.Current.CancellationToken);
        var tokenA2 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken);
        var tokenB2 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta", TestContext.Current.CancellationToken);

        Assert.Equal("token-for-sa-alpha", tokenA1);
        Assert.Equal("token-for-sa-beta", tokenB1);
        Assert.NotEqual(tokenA1, tokenB1);

        // Cache hits: same token returned, no additional acquisitions.
        Assert.Equal(tokenA1, tokenA2);
        Assert.Equal(tokenB1, tokenB2);
        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NeverTouchesTheGlobalServiceCredential()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, ValidToken);

        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken);
        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta", TestContext.Current.CancellationToken);

        A.CallToSet(() => globalToken.AccessToken).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenWithinExpiryBuffer_IsReacquired()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        // Expires in 30s — inside the 60s buffer, so the cache entry is never valid.
        var sut = new StubTokenService(globalToken,
            name => new ServiceAccountTokenService.CachedToken(
                $"token-for-{name}", DateTime.UtcNow.AddSeconds(30)));

        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken);
        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken);

        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AcquisitionFailure_ReturnsNullAndIsNotNegativelyCached()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, _ => null);

        var first = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-broken", TestContext.Current.CancellationToken);
        var second = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-broken", TestContext.Current.CancellationToken);

        Assert.Null(first);
        Assert.Null(second);
        // A transient identity-server outage must not poison the cache — each call retries.
        Assert.Equal(2, sut.AcquireCount);
        A.CallToSet(() => globalToken.AccessToken).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallsForOneKey_AcquireOnceAndShareTheToken()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new StubTokenService(globalToken, ValidToken) { Gate = gate };

        // Five concurrent pipeline executions hit an empty cache for the same (tenant, name).
        var calls = Enumerable.Range(0, 5)
            .Select(_ => sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", TestContext.Current.CancellationToken))
            .ToArray();
        // The first caller is inside the acquisition, the others are queued on the lock. Bounded:
        // if acquisition never starts the test must fail with a diagnostic, not hang.
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sut.AcquireCount == 0)
        {
            Assert.False(startTimeout.IsCancellationRequested, "AcquireTokenAsync was not entered within 5 s");
            await Task.Delay(10, CancellationToken.None);
        }

        gate.SetResult();
        var tokens = await Task.WhenAll(calls);

        // Single-flight: one round trip to the identity server, every caller gets that token.
        Assert.Equal(1, sut.AcquireCount);
        Assert.All(tokens, t => Assert.Equal("token-for-sa-alpha", t));
    }

    [Fact]
    public async Task GetAccessTokenAsync_StalledAcquisitionForOneKey_DoesNotBlockAnotherKey()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new StubTokenService(globalToken, ValidToken) { Gate = gate, GatedName = "sa-slow" };

        // The identity server hangs for sa-slow ...
        var slow = sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-slow", TestContext.Current.CancellationToken);
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sut.AcquireCount == 0)
        {
            Assert.False(startTimeout.IsCancellationRequested, "AcquireTokenAsync was not entered within 5 s");
            await Task.Delay(10, CancellationToken.None);
        }

        // ... while an unrelated key must still be served. Bounded so a regression to a global
        // lock fails with a diagnostic instead of hanging the run.
        var fast = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-fast", TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("token-for-sa-fast", fast);
        Assert.False(slow.IsCompleted);

        gate.SetResult();
        Assert.Equal("token-for-sa-slow", await slow);
        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AcquisitionExceedsTheFlightTimeout_FailsAndReleasesTheKeyForRetry()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        // A gate that is never opened: the identity server hangs.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new StubTokenService(globalToken, ValidToken)
        {
            Gate = gate,
            GatedName = "sa-hanging",
            AcquisitionTimeout = TimeSpan.FromMilliseconds(200)
        };

        // The flight is bound even though the caller itself never cancels ...
        await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-hanging", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, sut.AcquireCount);

        // ... and once the cancelled work has wound down the key is free again: the next caller
        // starts a fresh attempt. (Between the timeout and the wind-down a caller sees the faulted
        // flight and fails fast — that window is exercised by the uncancellable-work test below.)
        gate.SetResult();
        string? token = null;
        using var retryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (token is null)
        {
            Assert.False(retryBudget.IsCancellationRequested, "the key was not released after the cancelled work completed");
            try
            {
                token = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-hanging", TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                await Task.Delay(10, CancellationToken.None);
            }
        }

        Assert.Equal("token-for-sa-hanging", token);
        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_UncancellableWorkOutlivesTheTimeout_LaterCallersFailFastUntilItCompletes()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new StubTokenService(globalToken, ValidToken)
        {
            Gate = gate,
            GatedName = "sa-hung-repo",
            GateIgnoresCancellation = true,
            AcquisitionTimeout = TimeSpan.FromSeconds(2)
        };
        var ct = TestContext.Current.CancellationToken;

        // The first caller times out while the (uncancellable) read is still running ...
        await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-hung-repo", ct).WaitAsync(TimeSpan.FromSeconds(5), ct));

        // ... and a second caller does NOT start another read against the hung repository: it fails
        // fast on the same flight.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-hung-repo", ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
        // Well below the 2 s acquisition timeout: a regression that started a new flight would wait it out.
        Assert.True(sw.ElapsedMilliseconds < 1000, $"second caller waited {sw.ElapsedMilliseconds} ms instead of failing fast");
        Assert.Equal(1, sut.AcquireCount);

        // Once the hung read completes the key is released and a fresh acquisition succeeds.
        gate.SetResult();
        string? token = null;
        using var retryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (token is null)
        {
            Assert.False(retryBudget.IsCancellationRequested, "the key was not released after the hung work completed");
            try
            {
                token = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-hung-repo", ct);
            }
            catch (TimeoutException)
            {
                await Task.Delay(10, CancellationToken.None);
            }
        }

        Assert.Equal("token-for-sa-hung-repo", token);
        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TenantIdContradictsTheRepository_IsRejectedBeforeAnyAcquisition()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, ValidToken);
        var repoOfTenantB = A.Fake<ITenantRepository>();
        A.CallTo(() => repoOfTenantB.TenantId).Returns("tenant-b");

        // Caching tenant B's token under tenant A's key would hand it to tenant A's pipelines later.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.GetAccessTokenAsync(repoOfTenantB, "tenant-a", "sa-alpha", TestContext.Current.CancellationToken));

        Assert.Equal(0, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_KeyIsCaseInsensitiveAndStructured_SharesTheTokenWithinOneTenantOnly()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, ValidToken);
        var ct = TestContext.Current.CancellationToken;

        var first = await sut.GetAccessTokenAsync(_repo, "Tenant-1", "SA-Alpha", ct);
        var second = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha", ct);
        // A delimiter-joined string key could alias these two pairs; a structured key cannot.
        var other = await sut.GetAccessTokenAsync(_repo, "tenant-1::sa", "alpha", ct);

        Assert.Equal(first, second);
        Assert.NotNull(other);
        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_SameNameDifferentTenants_NeverSharesTokens()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        // Mint returns a unique token per acquisition so cache collisions are observable.
        var mintCounter = 0;
        var sut = new StubTokenService(globalToken,
            _ => new ServiceAccountTokenService.CachedToken(
                $"token-{++mintCounter}", DateTime.UtcNow.AddHours(1)));

        var tenant1Token = await sut.GetAccessTokenAsync(_repo, "tenant-1", "mcp-sa", TestContext.Current.CancellationToken);
        var tenant2Token = await sut.GetAccessTokenAsync(_repo, "tenant-2", "mcp-sa", TestContext.Current.CancellationToken);
        var tenant1Again = await sut.GetAccessTokenAsync(_repo, "tenant-1", "mcp-sa", TestContext.Current.CancellationToken);

        // Same wellKnownName, different tenants: two distinct acquisitions, no sharing.
        Assert.Equal("token-1", tenant1Token);
        Assert.Equal("token-2", tenant2Token);
        Assert.NotEqual(tenant1Token, tenant2Token);

        // And per-tenant caching still works.
        Assert.Equal(tenant1Token, tenant1Again);
        Assert.Equal(2, sut.AcquireCount);
    }
}

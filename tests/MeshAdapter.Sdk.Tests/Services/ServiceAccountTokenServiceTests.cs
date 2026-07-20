using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
/// Pins the AB#4315 consolidation contracts of <see cref="ServiceAccountTokenService"/>:
/// (1) tokens are cached PER configuration name — two service accounts on one adapter
/// never receive each other's token (the pre-consolidation identity-blind cache bug);
/// (2) <c>GetAccessTokenAsync</c> never touches the adapter-global
/// <c>IServiceClientAccessToken</c> (the pre-consolidation clobbering bug);
/// (3) <c>EnsureTokenAsync</c> DOES publish to the global credential — that side effect
/// is the deploy path's contract, not a bug;
/// (4) expired tokens are re-acquired; failures are not negatively cached.
/// The network round-trip is substituted via the protected virtual
/// <c>AcquireTokenAsync</c> seam; the cache/side-effect logic under test is real.
/// </summary>
public class ServiceAccountTokenServiceTests
{
    private sealed class StubTokenService(
        IServiceClientAccessToken globalToken,
        Func<string, ServiceAccountTokenService.CachedToken?> mint)
        : ServiceAccountTokenService(globalToken, NullLogger<ServiceAccountTokenService>.Instance)
    {
        public int AcquireCount { get; private set; }

        protected override Task<CachedToken?> AcquireTokenAsync(ITenantRepository tenantRepository,
            string wellKnownName, CancellationToken cancellationToken)
        {
            AcquireCount++;
            return Task.FromResult(mint(wellKnownName));
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

        var tokenA1 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha");
        var tokenB1 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta");
        var tokenA2 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha");
        var tokenB2 = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta");

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

        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha");
        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-beta");

        A.CallToSet(() => globalToken.AccessToken).MustNotHaveHappened();
    }

    [Fact]
    public async Task EnsureTokenAsync_PublishesTheTokenToTheGlobalServiceCredential()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, ValidToken);

        await sut.EnsureTokenAsync(_repo, "tenant-1", "sa-deploy");

        A.CallToSet(() => globalToken.AccessToken).To("token-for-sa-deploy")
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenWithinExpiryBuffer_IsReacquired()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        // Expires in 30s — inside the 60s buffer, so the cache entry is never valid.
        var sut = new StubTokenService(globalToken,
            name => new ServiceAccountTokenService.CachedToken(
                $"token-for-{name}", DateTime.UtcNow.AddSeconds(30)));

        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha");
        await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-alpha");

        Assert.Equal(2, sut.AcquireCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AcquisitionFailure_ReturnsNullAndIsNotNegativelyCached()
    {
        var globalToken = A.Fake<IServiceClientAccessToken>();
        var sut = new StubTokenService(globalToken, _ => null);

        var first = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-broken");
        var second = await sut.GetAccessTokenAsync(_repo, "tenant-1", "sa-broken");

        Assert.Null(first);
        Assert.Null(second);
        // A transient identity-server outage must not poison the cache — each call retries.
        Assert.Equal(2, sut.AcquireCount);
        A.CallToSet(() => globalToken.AccessToken).MustNotHaveHappened();
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

        var tenant1Token = await sut.GetAccessTokenAsync(_repo, "tenant-1", "mcp-sa");
        var tenant2Token = await sut.GetAccessTokenAsync(_repo, "tenant-2", "mcp-sa");
        var tenant1Again = await sut.GetAccessTokenAsync(_repo, "tenant-1", "mcp-sa");

        // Same wellKnownName, different tenants: two distinct acquisitions, no sharing.
        Assert.Equal("token-1", tenant1Token);
        Assert.Equal("token-2", tenant2Token);
        Assert.NotEqual(tenant1Token, tenant2Token);

        // And per-tenant caching still works.
        Assert.Equal(tenant1Token, tenant1Again);
        Assert.Equal(2, sut.AcquireCount);
    }
}

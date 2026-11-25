using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>The registry snapshot: parents, the system tenant, cache and refresh limits, fail closed.</summary>
public class TenantRegistryReaderTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private List<OctoTenant> _rows = [new("g", "dbg"), new("a", "dba", "g")];

    public TenantRegistryReaderTests()
    {
        A.CallTo(() => _systemContext.GetAllTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .ReturnsLazily(() => Task.FromResult<IResultSet<OctoTenant>>(
                new ResultSet<OctoTenant>(_rows.ToList(), _rows.Count, null, null)));
    }

    private TenantRegistryReader CreateReader() =>
        new(_systemContext, Options.Create(new OctoSystemConfiguration { SystemTenantId = "octosystem" }), _time);

    [Fact]
    public async Task Snapshot_KnowsParents_AndHangsRowsWithoutParentBelowTheSystemTenant()
    {
        var snapshot = await CreateReader().GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("g", snapshot.ParentOf("a"));
        Assert.Equal("octosystem", snapshot.ParentOf("g"));
        Assert.Null(snapshot.ParentOf("octosystem"));
        Assert.True(snapshot.IsRegistered("A"));
        Assert.False(snapshot.IsRegistered("d"));
    }

    [Fact]
    public async Task Snapshot_IsCachedFor30Seconds_AndARefreshReadsAtMostOncePerSecond()
    {
        var reader = CreateReader();
        var ct = TestContext.Current.CancellationToken;
        await reader.GetAsync(cancellationToken: ct);

        _rows = [.. _rows, new OctoTenant("b", "dbb", "g")];
        Assert.False((await reader.GetAsync(cancellationToken: ct)).IsRegistered("b"));
        // A refresh within the first second still answers from the snapshot.
        Assert.False((await reader.GetAsync(refresh: true, cancellationToken: ct)).IsRegistered("b"));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await reader.GetAsync(refresh: true, cancellationToken: ct)).IsRegistered("b"));

        _rows = [.. _rows, new OctoTenant("c", "dbc", "g")];
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await reader.GetAsync(cancellationToken: ct)).IsRegistered("c"));
    }

    [Fact]
    public async Task UnreadableRegistry_Throws_AndIsNeverAnEmptyPass()
    {
        A.CallTo(() => _systemContext.GetAllTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Throws(new TimeoutException("mongo"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            CreateReader().GetAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}

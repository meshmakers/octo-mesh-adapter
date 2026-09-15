using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#4924 — <b>the borrower's database credential arrives with the lease and leaves with it.</b>
/// </summary>
/// <remarks>
///     <para>
///         🔴 The operator refuses an adapter pool the cluster's shared data-store credentials because
///         "tenant-scoped data access arrives with the lease and leaves with it". These tests are the
///         member's half of that sentence: it holds the credential for one database while one lease
///         runs, it holds it for no other database even then, and it holds nothing at all afterwards.
///     </para>
///     <para>
///         The <i>whole</i> sentence — that the credential is what actually opens the borrower's
///         database — needs a real MongoDB with a real per-tenant password and lives in
///         <c>LeasedTenantIsolationTests</c>. What is provable here is the state machine, and it is
///         provable without a container.
///     </para>
/// </remarks>
public class BorrowerDatabaseLeaseParticipantTests
{
    private const string BorrowerTenantId = "tenant-b";
    private const string BorrowerDatabaseName = "dbtenantb";
    private const string BorrowerDatabaseUser = "octo-system-ds-user-dbtenantb";
    private const string BorrowerDatabasePassword = "dbPwd-Qv7Xr2Mn8Kt4Ws0Yh3Bd6Lp9Cf1Zg5Ja";

    private readonly LeasedDatabaseCredentialSource _source = new();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    private BorrowerDatabaseLeaseParticipant Participant =>
        new(_source, _systemContext, NullLogger<BorrowerDatabaseLeaseParticipant>.Instance);

    private static LeaseDto ALease(string? databaseName = BorrowerDatabaseName,
        string? user = BorrowerDatabaseUser, string? password = BorrowerDatabasePassword) => new()
    {
        LeaseId = "lease-1",
        TenantId = BorrowerTenantId,
        PoolTenantId = "lender",
        PoolRtId = "665f0000000000000000ee21",
        AdapterRtId = "665f0000000000000000ee22",
        AdapterCkTypeId = "System.Communication/Adapter",
        ClientId = "octo-pipeline-sa-tenant-b",
        ClientSecret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm",
        DatabaseName = databaseName ?? string.Empty,
        DatabaseUser = user ?? string.Empty,
        DatabasePassword = password ?? string.Empty,
        GrantedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };

    /// <summary>
    ///     🔴 During the lease the member can open the borrower's database, <b>as the user the
    ///     controller named</b> — not one it formatted itself.
    /// </summary>
    [Fact]
    public async Task DuringTheLease_TheCredentialIsAvailableForTheBorrowersDatabase()
    {
        await Participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        Assert.True(_source.TryGetCredential(BorrowerDatabaseName, out var user, out var password));
        Assert.Equal(BorrowerDatabaseUser, user);
        Assert.Equal(BorrowerDatabasePassword, password);
    }

    /// <summary>
    ///     🔴 <b>And for no other database, not even while the lease is held.</b> A pool member opens
    ///     more than the borrower's database — the installation's tenant registry above all — and this
    ///     is the assertion that keeps the credential from being presented to any of them. A source
    ///     that answered unconditionally would work perfectly in every happy-path test and would be
    ///     the mechanism by which a neighbour tenant's database is opened with a credential that was
    ///     never meant for it.
    /// </summary>
    [Theory]
    [InlineData("dbtenanta")]
    [InlineData("OctoSystem")]
    [InlineData("")]
    public async Task DuringTheLease_TheCredentialIsAvailableForNoOtherDatabase(string otherDatabase)
    {
        await Participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        Assert.False(_source.TryGetCredential(otherDatabase, out var user, out var password));
        Assert.Equal(string.Empty, user);
        Assert.Equal(string.Empty, password);
    }

    /// <summary>
    ///     Database names are matched the way MongoDB names them here — the tenant record's spelling,
    ///     case-insensitively, the same comparison the engine's own database lookups use.
    /// </summary>
    [Fact]
    public async Task TheDatabaseIsMatchedIgnoringCase()
    {
        await Participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        Assert.True(_source.TryGetCredential("DbTenantB", out _, out _));
    }

    /// <summary>
    ///     🔴 <b>After the release the member holds nothing.</b> The post-release state assertion, in
    ///     the same form as increment 6's: not "it was dropped somewhere" but "asking for it answers
    ///     no".
    /// </summary>
    [Fact]
    public async Task AfterTheRelease_TheMemberHoldsNoDatabaseCredential()
    {
        var lease = ALease();
        await Participant.EnterLeaseAsync(lease, CancellationToken.None);

        await Participant.LeaveLeaseAsync(lease, CancellationToken.None);

        Assert.False(_source.TryGetCredential(BorrowerDatabaseName, out _, out _));
        Assert.Null(_source.HeldDatabaseName);
    }

    /// <summary>
    ///     🔴 The cached repository client — and the authenticated connection pool underneath it — is
    ///     dropped on <b>both</b> edges. On enter because the engine builds a client's connection, with
    ///     its credential, exactly once and caches it per database: a client left from an earlier lease
    ///     would keep authenticating with that lease's credential and this one's would never be used.
    ///     On leave because a live authenticated pool pointing at the released tenant's database is
    ///     precisely the thing "retains nothing tenant-scoped" is about.
    /// </summary>
    [Fact]
    public async Task TheCachedConnectionsAreDroppedOnBothEdgesOfTheLease()
    {
        var lease = ALease();

        await Participant.EnterLeaseAsync(lease, CancellationToken.None);
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(BorrowerTenantId,
            BorrowerDatabaseName, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await Participant.LeaveLeaseAsync(lease, CancellationToken.None);
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(BorrowerTenantId,
            BorrowerDatabaseName, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    /// <summary>
    ///     🔴 An eviction that fails on release is <b>not</b> swallowed. A member that cannot prove it
    ///     dropped the released tenant's connections must drain rather than take another lease
    ///     (concept §6) — and <c>AdapterPoolClient</c> turns a throwing leave into exactly that. The
    ///     credential itself is gone either way: dropped before the eviction is attempted.
    /// </summary>
    [Fact]
    public async Task AFailedEvictionOnReleaseSurfaces_ButTheCredentialIsGoneAnyway()
    {
        var lease = ALease();
        await Participant.EnterLeaseAsync(lease, CancellationToken.None);

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(BorrowerTenantId,
                BorrowerDatabaseName, A<CancellationToken>._))
            .Throws(new InvalidOperationException("mongo unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Participant.LeaveLeaseAsync(lease, CancellationToken.None));

        Assert.False(_source.TryGetCredential(BorrowerDatabaseName, out _, out _));
    }

    /// <summary>
    ///     🔴 <b>A lease that carries no database credential is refused by the member too.</b> Two
    ///     gates, one on each side of the wire: the controller refuses to grant one, and the member
    ///     refuses to take one. A member that shrugged would execute the borrower's work on whatever
    ///     credentials its own process happens to hold — everything on a developer's machine, nothing
    ///     in a cluster, and in neither case the borrower's — while looking perfectly healthy.
    /// </summary>
    [Theory]
    [InlineData(null, BorrowerDatabaseUser, BorrowerDatabasePassword)]
    [InlineData(BorrowerDatabaseName, null, BorrowerDatabasePassword)]
    [InlineData(BorrowerDatabaseName, BorrowerDatabaseUser, null)]
    public async Task ALeaseWithoutACompleteDatabaseCredential_IsRefused(string? databaseName, string? user,
        string? password)
    {
        var lease = ALease(databaseName, user, password);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Participant.EnterLeaseAsync(lease, CancellationToken.None));

        // Names the tenant so an operator can act; never the value.
        Assert.Contains(BorrowerTenantId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BorrowerDatabasePassword, exception.Message, StringComparison.Ordinal);
        // And nothing was installed on the way out.
        Assert.Null(_source.HeldDatabaseName);
    }

    /// <summary>
    ///     🔴 The credential holder renders no secret. It is a singleton in a DI graph, one
    ///     <c>LogDebug</c> away from being printed, and a record's generated <c>ToString</c> prints
    ///     every member.
    /// </summary>
    [Fact]
    public async Task TheCredentialSourceNeverRendersWhatItHolds()
    {
        await Participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        var rendered = _source.ToString();

        Assert.DoesNotContain(BorrowerDatabasePassword, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(BorrowerDatabasePassword[..8], rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(BorrowerDatabaseUser, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    ///     🔴 <b>The participant order, asserted rather than described.</b> The prose in
    ///     <c>AddOctoMeshAdapterPoolMember</c> explains why the database credential sits between
    ///     identity and the CK cache; a reordering edit would not break a single other test — the CK
    ///     warm-up would simply start failing to authenticate, which reads like an infrastructure
    ///     problem and not like a code change.
    /// </summary>
    [Fact]
    public void TheParticipantsAreEnteredInTheDocumentedOrder()
    {
        var services = new ServiceCollection();
        services.AddOctoMeshAdapterPoolMember();

        var order = services
            .Where(d => d.ServiceType == typeof(IAdapterLeaseParticipant))
            .Select(d => d.ImplementationType!.Name)
            .ToArray();

        Assert.Equal(
        [
            nameof(BorrowerIdentityLeaseParticipant),
            nameof(BorrowerDatabaseLeaseParticipant),
            nameof(CkModelCacheLeaseParticipant),
            nameof(PipelineRegistryLeaseParticipant)
        ], order);
    }

    /// <summary>
    ///     The engine reads the credential through <c>ITenantDatabaseCredentialSource</c> and the
    ///     participant writes it through <c>LeasedDatabaseCredentialSource</c>. They must be the
    ///     <b>same instance</b> — two would give a member that looks configured and authenticates with
    ///     nothing, and nothing about that fails at startup.
    /// </summary>
    [Fact]
    public void TheEngineReadsTheSameCredentialHolderTheParticipantWritesTo()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOctoMeshAdapterPoolMember();
        using var provider = services.BuildServiceProvider();

        var written = provider.GetRequiredService<LeasedDatabaseCredentialSource>();
        var read = provider.GetRequiredService<ITenantDatabaseCredentialSource>();

        Assert.Same(written, read);
    }
}

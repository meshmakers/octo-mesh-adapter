using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Gives the member access to the borrowing tenant's <b>data</b> for the duration of the lease,
///     and takes it away again on release (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>This is the participant that makes the operator's sentence true.</b>
///         <c>WorkloadReconciler.AppendClusterSecrets</c> refuses an adapter pool the cluster's shared
///         data-store credentials — one Mongo user, one CrateDB user, every tenant's data behind them —
///         with the reasoning that "tenant-scoped data access arrives with the lease and leaves with
///         it". Until this existed that was aspiration: the lease carried an OAuth credential and
///         nothing else, so a mesh-adapter pool member could execute no pipeline that touched an RT
///         entity, and the one end-to-end run that appeared to work did so only because the process was
///         started by hand and inherited a developer's Mongo credentials from its environment.
///     </para>
///     <para>
///         <b>Where it sits in the participant order, and why.</b> Second — after
///         <see cref="BorrowerIdentityLeaseParticipant" /> and before
///         <see cref="CkModelCacheLeaseParticipant" />.
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>After identity</b> because identity is the gate. A member that cannot become the
///                 borrower must not touch its data at all, and installing a live credential to a
///                 tenant's database before knowing whether the token exchange succeeds would put that
///                 credential into the process for a lease that is about to fail. The existing order
///                 already says "identity first because the CK cache warm-up reads the tenant"; the
///                 same sentence, read one step earlier, says the database credential comes after it
///                 too.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Before the CK cache</b> because the CK cache warm-up is the first thing that
///                 opens the borrower's database — <c>FindTenantRepositoryAsync</c> — and it cannot
///                 open it without this. This is not a preference: with the order reversed the lease
///                 fails on its first Mongo command, every time.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>And therefore, on leave, dropped third of four</b> (participants leave in reverse
///                 order): after the pipeline registrations and the CK model are gone, and before the
///                 borrower's token. That is the right end too — the CK-cache leave is the last step
///                 that could still need to reach the borrower's database, and the credential is gone
///                 before the process is declared tenant-free.
///             </description>
///         </item>
///     </list>
///     <para>
///         🔴 <b>A lease without a database credential is refused here as well as at the controller.</b>
///         Two gates, one on each side of the wire, for the same reason the pool's cluster-secret
///         refusal has two: a member that shrugged and carried on would run the borrower's pipeline
///         against whatever credentials its own process happens to hold, which is precisely the
///         failure this mechanism removes — and it would look perfectly healthy while doing it.
///     </para>
///     <para>
///         <b>Stream data (CrateDB) is not here, deliberately.</b> There is no per-tenant CrateDB
///         principal to install: the engine holds one connection string per installation and separates
///         tenants by schema. Carrying that one credential on the lease would be time-scoped but not
///         tenant-scoped — the shape of this mechanism without its substance. Until a per-tenant
///         CrateDB user exists, a leased pipeline that writes an archive fails to connect rather than
///         reaching another tenant's schema. See <c>TenantDatabaseCredentialResolver</c> for the full
///         reasoning and what has to exist first.
///     </para>
/// </remarks>
internal sealed class BorrowerDatabaseLeaseParticipant(
    LeasedDatabaseCredentialSource credentialSource,
    ISystemContext systemContext,
    ILogger<BorrowerDatabaseLeaseParticipant> logger) : IAdapterLeaseParticipant
{
    /// <inheritdoc />
    public async Task EnterLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        // Named individually and never the value — the same rule the identity participant follows.
        if (string.IsNullOrWhiteSpace(lease.DatabaseName)
            || string.IsNullOrWhiteSpace(lease.DatabaseUser)
            || string.IsNullOrWhiteSpace(lease.DatabasePassword))
        {
            throw new InvalidOperationException(
                $"The lease for tenant '{lease.TenantId}' carries no database credential, so this pool "
                + "member has no authorised way to reach that tenant's data. Taking the lease anyway would "
                + "run the borrower's work on whatever credentials this process happens to hold. The lease "
                + "is refused.");
        }

        // The tenant id travels with it: the same object answers "where does this tenant live" for the
        // engine's resolve, which is what stops that resolve from reaching the installation's registry
        // — and therefore what stops this member from needing the installation's admin credential.
        credentialSource.HoldForLease(lease.TenantId, lease.DatabaseName, lease.DatabaseUser,
            lease.DatabasePassword);

        // 🔴 Evict before the first connection is built, not only on leave. The engine caches one
        // repository client per database for the life of the process and builds its connection —
        // credential included — exactly once, when the client is first created. A client left over
        // from an earlier lease of the same tenant would therefore keep authenticating with the
        // credential that lease carried, and this one's would never be used (AB#4690 is the same
        // mechanism seen from the tenant-recreate side).
        await systemContext.InvalidateTenantRepositoryClientsAsync(lease.TenantId, lease.DatabaseName,
            cancellationToken);

        logger.LogInformation(
            "This pool member may now open database '{DatabaseName}' of leased tenant '{TenantId}' as "
            + "'{DatabaseUser}'",
            lease.DatabaseName, lease.TenantId, lease.DatabaseUser);
    }

    /// <inheritdoc />
    public async Task LeaveLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        // Credential first, cache second. If the eviction below throws, the member must at least no
        // longer be able to build a NEW authenticated connection to the released tenant.
        credentialSource.Drop();

        // 🔴 And the connections already open have to go with it. Dropping the credential alone would
        // leave a cached, authenticated client — and its live connection pool — pointing at the
        // released tenant's database inside a process that serves another tenant a moment later. That
        // is the same object the post-release assertions call "nothing tenant-scoped retained".
        //
        // The database name comes from the lease rather than from the tenant record on purpose: the
        // record lives in the installation's registry, and looking it up here would make the release
        // path depend on a read this member may not be able to make.
        //
        // Deliberately not swallowed. A member that could not prove it dropped the tenant's
        // connections drains instead of taking another lease (concept §6) — AdapterPoolClient turns a
        // throwing leave into exactly that.
        await systemContext.InvalidateTenantRepositoryClientsAsync(lease.TenantId, lease.DatabaseName,
            cancellationToken);

        logger.LogDebug("Database credential and cached connections dropped for released tenant '{TenantId}'",
            lease.TenantId);
    }
}

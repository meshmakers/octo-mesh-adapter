using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Loads the borrower's CK model when a lease starts and <b>unloads it</b> when the lease ends
///     (AB#4924, concept §4 and implementation plan §12.4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>This is the cache the concept actually meant.</b> Concept §4 says
///         "<c>FindTenantRepositoryAsync</c> caches per process today" — it does not (§12.4): both the
///         tenant context and the tenant repository are constructed fresh on every call. What <i>is</i>
///         process-wide is <see cref="ICkCacheService" />, the CK model cache, and it is keyed by
///         tenant — so the risk it carries is <b>memory</b>, not leakage: a member that leased thirty
///         tenants would hold thirty tenant models.
///     </para>
///     <para>
///         That distinction matters for anyone auditing this: somebody hunting for a repository cache
///         will not find one and may conclude the invariant is already satisfied. It is not — the model
///         cache has to be unloaded, and <c>MeshAdapterService</c> already does exactly this on
///         <c>CkModelChanged</c>, so the mechanism exists and is merely being reused per lease.
///     </para>
///     <para>
///         The eager warm-up on enter is the AB#4920 measurement applied per lease rather than per
///         process: the first execution of a lease would otherwise pay the model load, and concept §4's
///         warm-up argument is the whole reason both dedicated and leased modes exist.
///     </para>
/// </remarks>
internal sealed class CkModelCacheLeaseParticipant(
    ICkCacheService ckCacheService,
    ISystemContext systemContext,
    ILogger<CkModelCacheLeaseParticipant> logger) : IAdapterLeaseParticipant
{
    /// <inheritdoc />
    public async Task EnterLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(lease.TenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        logger.LogDebug("CK model cache warmed for leased tenant '{TenantId}'", lease.TenantId);
    }

    /// <inheritdoc />
    public Task LeaveLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        if (ckCacheService.IsTenantLoaded(lease.TenantId))
        {
            ckCacheService.Unload(lease.TenantId);
        }

        // The three static attempt guards in TenantContext (service-managed CK models, ownership
        // stamp, stream-data auto-import) are likewise keyed by tenant and would otherwise grow
        // without bound across leases. Dropping them costs one extra resolve on the next lease of the
        // same tenant and keeps the member's footprint a function of concurrency rather than of how
        // many tenants it has ever served.
        systemContext.InvalidateTenantResolveImportGuards(lease.TenantId);

        logger.LogDebug("CK model cache unloaded for released tenant '{TenantId}'", lease.TenantId);
        return Task.CompletedTask;
    }
}

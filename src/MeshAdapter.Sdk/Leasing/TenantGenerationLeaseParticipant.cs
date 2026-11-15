using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Drops the released tenant's cached tenant generation when a lease ends (AB#9022).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ITenantGenerationReader" /> (octo-common-services, AB#8999) caches the current generation of each
///         tenant id it was asked for: the creation time of the tenant's lifecycle record, keyed by tenant id, at most
///         60 s, at most 1024 tenants. Keyed, so a lookup for tenant B never reads tenant A's entry, and it is no
///         credential and no tenant data. It is still per-tenant state, and the tenant-freedom rule of this repository
///         is that per-tenant state leaves with the lease: this participant calls
///         <see cref="ITenantGenerationReader.Invalidate" /> for the released tenant, so a member keeps no entry of a
///         tenant it no longer serves.
///     </para>
///     <para>
///         It is registered first and therefore left last: the entry is dropped after every other participant that
///         could still lead to a read of it has left.
///     </para>
///     <para>
///         The reader is registered by <c>AddOctoServiceInfrastructure()</c>, which <c>AddOctoMeshAdapter()</c> runs.
///         A composition without it (a pool member that does not run the domain registration) gets no reader and the
///         participant has nothing to drop.
///     </para>
/// </remarks>
internal sealed class TenantGenerationLeaseParticipant(ITenantGenerationReader? tenantGenerationReader = null)
    : IAdapterLeaseParticipant
{
    /// <inheritdoc />
    public Task EnterLeaseAsync(LeaseDto lease, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task LeaveLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        tenantGenerationReader?.Invalidate(lease.TenantId);
        return Task.CompletedTask;
    }
}

using Meshmakers.Octo.Sdk.Common.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Turns a mesh adapter host into an adapter <b>pool member</b> (AB#4924, increment 6).
/// </summary>
public static class MeshAdapterPoolServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the SDK's pool-member composition plus the mesh adapter's own lease participants.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>Call this BEFORE <c>AddOctoMeshAdapter()</c>.</b> The SDK registers
    ///         <c>IAdapterTenantScope</c> with <c>TryAddSingleton</c>, so whichever runs first wins —
    ///         and on a pool member the winner must be the lease-aware scope. Registering the dedicated
    ///         one instead fails nowhere: it simply never enforces the lease, and every execution looks
    ///         fine.
    ///     </para>
    ///     <para>
    ///         <b>The participant order is the order things are entered, and its reverse is the order
    ///         they are left.</b> Identity first, because the CK cache warm-up reads the tenant and a
    ///         member that cannot become the borrower must not touch its data at all; the pipeline
    ///         registry last, because it is the one that holds the borrower's credentials once a
    ///         pipeline is deployed and therefore has to be the <i>first</i> thing dropped.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddOctoMeshAdapterPoolMember(this IServiceCollection services)
    {
        services.AddAdapterPoolMember();

        services.AddSingleton<IAdapterLeaseParticipant, BorrowerIdentityLeaseParticipant>();
        services.AddSingleton<IAdapterLeaseParticipant, CkModelCacheLeaseParticipant>();
        services.AddSingleton<IAdapterLeaseParticipant, PipelineRegistryLeaseParticipant>();

        services.TryAddSingleton<IAdapterLeaseWorkItem, NoAdapterLeaseWorkItem>();

        return services;
    }
}

using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
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
    ///         🔴 <b>And <c>IContextCreatorService</c> must be the mesh one, which this method cannot
    ///         guarantee.</b> <c>AddDataPipeline()</c> registers the SDK's
    ///         <c>DefaultContextCreatorService</c> with a plain <c>AddSingleton</c>, so the last
    ///         registration wins — and this method has to run <i>before</i> it, for the reason above.
    ///         <c>AddOctoMeshAdapter()</c> registers <c>MeshContextCreatorService</c> after
    ///         <c>AddDataPipeline()</c> and therefore settles it for every real host. A composition that
    ///         skips <c>AddOctoMeshAdapter()</c> has to register it itself, after the pipeline, or every
    ///         lease fails with <i>"Etl context type mismatch. Expected IMeshEtlContext"</i> —
    ///         <c>LeasedPipelineWorkItem</c> builds an <c>IMeshEtlContext</c> and the default creator
    ///         cannot produce one.
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
        // 🔴 AB#4924 §9.9 / D4 — what a leased member actually runs, and it MUST be registered BEFORE
        // AddAdapterPoolMember(). That call TryAdds the SDK's NoAdapterLeaseWorkItem, and TryAdd means
        // whichever ran first wins: registering afterwards is a silent no-op that leaves every lease
        // reporting "nothing to run" while looking perfectly healthy. Same trap, same shape, as the
        // IAdapterTenantScope ordering documented above. TryAdd here rather than Add so a host or a
        // test that registered its own work item before calling this still keeps it.
        services.TryAddSingleton<IAdapterLeaseWorkItem, LeasedPipelineWorkItem>();

        services.AddAdapterPoolMember();

        services.AddSingleton<IAdapterLeaseParticipant, BorrowerIdentityLeaseParticipant>();
        services.AddSingleton<IAdapterLeaseParticipant, CkModelCacheLeaseParticipant>();
        services.AddSingleton<IAdapterLeaseParticipant, PipelineRegistryLeaseParticipant>();

        // 🔴 A pool member is a composition in its own right — the same finding increment 6 recorded
        // when AddAdapterPoolMember() had to register IPipelineRegistryService itself. The token
        // service normally arrives with AddOctoMeshAdapter(), which a pool member need not run, and
        // MeshContextCreatorService resolves it while building the per-execution identity resolver.
        services.TryAddSingleton<IServiceAccountTokenService, ServiceAccountTokenService>();

        return services;
    }
}

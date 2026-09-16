using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
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
    ///     <para>
    ///         🔴 <b>The database credential goes second — between identity and the CK cache — and the
    ///         position is forced from both sides.</b> It has to come <i>after</i> identity because
    ///         identity is the gate: a member that cannot become the borrower must not be holding a
    ///         live credential to that tenant's data while it finds out. It has to come <i>before</i>
    ///         the CK cache because the CK cache warm-up is the first thing that opens the borrower's
    ///         database, and it cannot open it without the credential — reversed, every lease fails on
    ///         its first Mongo command. The leave order follows: the credential and the cached
    ///         connections are dropped after the registrations and the model are gone and before the
    ///         token is cleared, which is the last moment anything could still legitimately need the
    ///         borrower's database.
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

        // 🔴 AB#4924 — the one object that holds where the borrowing tenant lives and how to open it,
        // and the two seams the runtime engine reads it through. Registered under its own type and
        // BOTH interfaces, and deliberately as the SAME instance: the participant writes to it while
        // the engine reads from it (SystemContext for the location, UserMongoRepositoryClient for the
        // credential), and separate instances would give a member that looks configured, resolves
        // nothing and authenticates with nothing.
        //
        // The pair is what keeps a member out of the installation's registry entirely. The location
        // half short-circuits the resolve that would otherwise run listDatabases on the ADMIN
        // connection and read the system database; the credential half opens the borrower's database
        // with what the lease carried. Either one alone leaves the member needing an
        // installation-wide credential.
        services.AddSingleton<LeasedDatabaseCredentialSource>();
        services.AddSingleton<ITenantDatabaseCredentialSource>(sp =>
            sp.GetRequiredService<LeasedDatabaseCredentialSource>());
        services.AddSingleton<ITenantLocationSource>(sp =>
            sp.GetRequiredService<LeasedDatabaseCredentialSource>());

        services.AddSingleton<IAdapterLeaseParticipant, BorrowerIdentityLeaseParticipant>();
        services.AddSingleton<IAdapterLeaseParticipant, BorrowerDatabaseLeaseParticipant>();
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

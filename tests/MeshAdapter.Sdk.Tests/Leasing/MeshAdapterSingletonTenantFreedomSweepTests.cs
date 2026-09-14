using System.Reflection;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#4924 increment 6, entry criterion 5 — the DI sweep on the <b>mesh adapter</b> side.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this has to exist here and not only in the SDK.</b> The SDK's
///         <c>SingletonTenantFreedomSweepTests</c> sweeps the service descriptors the SDK registers.
///         It is structurally blind to everything an adapter repository registers itself — and this
///         repository is where the caches actually are: <c>ICkCacheService</c>, the service-account
///         token cache, the mesh context creator, the HTTP request service's route prefix. A sweep
///         that could not see them would be a green light for exactly the regression it exists to
///         catch.
///     </para>
///     <para>
///         The rule is inverted on purpose: a singleton whose surface mentions a tenant is <b>guilty
///         until somebody has looked at it</b>. Adding one therefore fails the build until a human has
///         written down why it is safe. That is the point — the regression this catches is the one
///         nobody is thinking about six months from now, on a process that by then serves thirty
///         tenants a day instead of one forever.
///     </para>
/// </remarks>
public class MeshAdapterSingletonTenantFreedomSweepTests
{
    /// <summary>
    ///     Singleton types cleared as safe on a leased process, each with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ClearedSingletons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ServiceAccountTokenService"] =
                "Its identity cache is keyed (TenantId, ClientId), so a lookup for tenant B can never "
                + "return tenant A's entry - isolation-safe by construction. The one thing it holds "
                + "that is NOT keyed is the ambient IServiceClientAccessToken, and that is emptied on "
                + "every release by BorrowerIdentityLeaseParticipant.",
            ["MeshContextCreatorService"] =
                "Resolves the tenant repository from pipelineRegistration.TenantId on every call and "
                + "keeps nothing between calls. The CK cache it warms is ICkCacheService, which "
                + "CkModelCacheLeaseParticipant unloads on release.",
            ["HttpRequestService"] =
                "Its route prefix comes from AdapterOptions.DedicatedTenantId, which a pool member "
                + "does not set - a member exposes no tenant-prefixed HTTP routes of its own. The "
                + "per-request tenant check reads the route, not a field.",
            ["BorrowerIdentityLeaseParticipant"] =
                "AB#4924. Holds the borrower credential only as a method parameter for the length of "
                + "EnterLeaseAsync, and empties the token holder on leave. "
                + "BorrowerIdentityLeaseParticipantTests pins both.",
            ["CkModelCacheLeaseParticipant"] =
                "AB#4924. Exists precisely to unload the released tenant's CK model; it holds no "
                + "state of its own.",
            ["LeasedPipelineWorkItem"] =
                "AB#4924 §9.9 / D4. Resolves the tenant from IAdapterTenantScope on every call and "
                + "keeps nothing between leases: the lease, its pipeline configuration and the "
                + "registration it produces are all locals. It deliberately does NOT read "
                + "LeaseDto.TenantId as its tenant - it checks the lease AGAINST the scope and fails "
                + "the lease on a disagreement, which is the same rule every service around the node "
                + "layer follows since increment 3. The registration it creates is dropped on release "
                + "by PipelineRegistryLeaseParticipant.",
            ["PipelineRegistryLeaseParticipant"] =
                "AB#4924. Exists precisely to drop the released tenant's pipeline registrations - "
                + "which is where the borrower's credentials live once a pipeline is deployed.",
            ["AdapterPoolTenantScope"] =
                "AB#4924. The lease tenant is process-wide by design and null between leases; an "
                + "execution for another tenant is refused. Pinned by AdapterPoolTenantScopeTests in "
                + "the SDK.",
            ["AdapterPoolClient"] =
                "AB#4924. Drives one lease at a time; the lease lives on IAdapterLeaseScope and the "
                + "LeaseDto is a parameter, not a field.",
            ["CkCacheService"] =
                "The CK model cache - the cache concept 4 actually meant (the tenant repository is "
                + "constructed fresh per call, see implementation plan 12.4). Keyed by tenant, so it "
                + "is isolation-safe by construction; the risk it carries is MEMORY, and "
                + "CkModelCacheLeaseParticipant unloads the released tenant on every release.",
            ["SystemContext"] =
                "Constructs a new TenantContext and TenantRepository on every call - there is no "
                + "repository cache (implementation plan 12.4). Its three static attempt guards are "
                + "keyed by tenant and are dropped per lease by CkModelCacheLeaseParticipant via "
                + "InvalidateTenantResolveImportGuards.",
            ["CrateDbConnectionAccess"] =
                "Caches one CrateDB datasource per tenant schema, keyed by the normalised tenant id, "
                + "so a query for tenant B can never reach tenant A's schema. Unbounded across "
                + "leases in the same way the CK cache was before this increment - a sockets/memory "
                + "question, not a leakage one. Noted in the increment 6 report as the next "
                + "candidate for a lease participant if a member ever leases stream-data tenants at "
                + "scale.",
            ["CrateDatabaseClient"] =
                "Derives the schema name from the tenantId ARGUMENT on every call (TenantSchema."
                + "SchemaName(tenantId)); it holds an instance prefix, never a tenant.",
            ["IStreamDataDatabaseManagementClient"] =
                "Factory alias onto CrateDatabaseClient, cleared above.",
            ["DefaultTenantNotifications"] =
                "Tenant lifecycle fan-out. The tenant is a method parameter of every member; nothing "
                + "is retained.",
            ["DistributedTenantNotifications"] =
                "Same as DefaultTenantNotifications, over the message bus instead of in process.",
            ["TenantSetupRetryStore"] =
                "Bookkeeping of failed tenant setups in a system-database collection. Its only "
                + "process state is a one-shot index guard; every read and write takes the tenant as "
                + "an argument.",
            ["DistributedCacheService"] =
                "Resolves the tenant through ITenantResolver per call and keeps nothing. On a pool "
                + "member there is no ambient HTTP tenant to resolve, which is a loud failure rather "
                + "than a wrong answer.",
            ["OctoHttpContextAccessor"] =
                "Reads the tenant off the CURRENT HTTP request and throws when there is none. A pool "
                + "member serves no tenant-addressed HTTP routes, so this cannot answer with a stale "
                + "tenant - it can only fail to answer.",
            ["TenantHierarchyReader"] =
                "Caches (child, ancestor) pairs of the tenant TREE, not tenant data - the same answer "
                + "for every process, with its own bounded eviction. Nothing a lease owns.",
        };

    /// <summary>
    ///     Singleton registrations made through a factory, cleared by SERVICE type with a reason.
    /// </summary>
    /// <remarks>
    ///     A factory hides the implementation type, which is exactly what this sweep exists to
    ///     inspect - so it is an offender unless somebody named it here. Both entries below are
    ///     aliases onto an instance that is itself cleared above, which is the only shape that
    ///     deserves the exemption.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ClearedFactorySingletons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IAdapterTenantScope"] =
                "Alias onto the single AdapterPoolTenantScope instance, cleared above.",
            ["IAdapterLeaseScope"] =
                "Alias onto the single AdapterPoolTenantScope instance, cleared above.",
            ["IStreamDataDatabaseManagementClient"] =
                "Alias onto the single CrateDatabaseClient instance, cleared above.",
        };

    /// <summary>
    ///     Members whose name mentions a tenant but which are demonstrably not retained state.
    /// </summary>
    private static readonly IReadOnlySet<string> IgnoredMemberNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Method parameters and per-call arguments are not retained state.
            "BeginExecution",
            "BeginLease",
            "EnterLeaseAsync",
            "LeaveLeaseAsync"
        };

    /// <summary>
    ///     🔴 The pool-member composition, which is the one where a retained tenant is a cross-tenant
    ///     read rather than a harmless cache.
    /// </summary>
    [Fact]
    public void EverySingletonOfThePoolMemberCompositionIsCleared()
    {
        var services = new ServiceCollection();
        services.AddOctoMeshAdapterPoolMember();

        var offenders = Sweep(services);

        Assert.True(offenders.Count == 0,
            "A singleton in the mesh adapter's POOL MEMBER composition touches a tenant without "
            + "having been cleared (AB#4924). Decide whether it retains tenant state across leases; "
            + "if it does not, add it to ClearedSingletons with the reason. Offenders: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    ///     The sweep must also see what <c>AddOctoMeshAdapter</c> itself registers — that is where the
    ///     domain singletons are, and a pool member runs all of them.
    /// </summary>
    [Fact]
    public void EveryDomainSingletonOfTheMeshAdapterIsCleared()
    {
        var services = new ServiceCollection();
        services.AddOctoMeshAdapterPoolMember();
        services.AddOctoMeshAdapter();

        var offenders = Sweep(services);

        Assert.True(offenders.Count == 0,
            "A singleton registered by AddOctoMeshAdapter touches a tenant without having been "
            + "cleared (AB#4924). This is the sweep the SDK-side one structurally cannot make: the "
            + "caches live here. Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    ///     An allow-list that keeps entries for types that no longer exist stops being read.
    /// </summary>
    [Fact]
    public void TheClearedListHasNoStaleEntries()
    {
        var services = new ServiceCollection();
        services.AddOctoMeshAdapterPoolMember();
        services.AddOctoMeshAdapter();

        var knownNames = services
            .Select(d => d.ImplementationType?.Name ?? d.ImplementationInstance?.GetType().Name)
            .Concat(services.Select(d => d.ServiceType.Name))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var stale = ClearedSingletons.Keys
            .Concat(ClearedFactorySingletons.Keys)
            .Where(name => !knownNames.Contains(name))
            .ToList();

        Assert.True(stale.Count == 0,
            "ClearedSingletons names types that the mesh adapter no longer registers: "
            + string.Join(", ", stale));
    }

    private static List<string> Sweep(IServiceCollection services)
    {
        var offenders = new List<string>();

        foreach (var descriptor in services)
        {
            if (descriptor.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            var implementationType = descriptor.ImplementationType
                                     ?? descriptor.ImplementationInstance?.GetType();
            if (implementationType is null)
            {
                // A factory registration hides its implementation type. It is only interesting here
                // when the SERVICE type itself mentions a tenant — otherwise the graph is full of
                // framework factories (HttpClient, options) that this sweep has nothing to say about.
                if (MentionsATenant(descriptor.ServiceType)
                    && !ClearedFactorySingletons.ContainsKey(descriptor.ServiceType.Name))
                {
                    offenders.Add($"{descriptor.ServiceType.Name}: singleton registered by factory "
                                  + "whose service type mentions a tenant; implementation not inspectable");
                }

                continue;
            }

            if (!MentionsATenant(implementationType))
            {
                continue;
            }

            if (!ClearedSingletons.ContainsKey(implementationType.Name))
            {
                offenders.Add($"{implementationType.Name}: singleton whose surface mentions a tenant "
                              + "and which is not on the cleared list");
            }
        }

        return offenders;
    }

    private static bool MentionsATenant(Type type)
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic
                                                        | BindingFlags.Instance | BindingFlags.Static
                                                        | BindingFlags.DeclaredOnly;

        return type.GetMembers(members)
                   .Where(m => !IgnoredMemberNames.Contains(m.Name))
                   .Any(m => m.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase))
               || type.GetFields(members)
                   .Any(f => f.FieldType.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }
}

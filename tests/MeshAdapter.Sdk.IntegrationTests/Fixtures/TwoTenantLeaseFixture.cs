using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     Two real tenants in one real MongoDB, each holding the same CK type with a <b>different</b>
///     marker value — the substrate of the AB#4924 increment 6 isolation suite.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why two databases and not two in-memory fakes.</b> The defect this suite exists to
///         catch is a process that carries something of tenant A into tenant B's execution: a warm CK
///         model, a token, a registration, a cached repository. Every one of those is a real object
///         built from a real connection, and a fake repository would answer correctly by construction
///         — the test would pass with the isolation mechanism removed, which is the one outcome that
///         must not be possible here.
///     </para>
///     <para>
///         Tenant A additionally holds a <see cref="PoisonCanary" />: a value that must never appear
///         in any other tenant's execution output. Cheap, and it fails loudly on a leak nobody
///         predicted.
///     </para>
/// </remarks>
public class TwoTenantLeaseFixture : SystemFixture
{
    /// <summary>The first borrowing tenant.</summary>
    public const string TenantA = "leasetenanta";

    /// <summary>The second borrowing tenant.</summary>
    public const string TenantB = "leasetenantb";

    /// <summary>A third one, so the randomised interleavings have more than two states to visit.</summary>
    public const string TenantC = "leasetenantc";

    /// <summary>CK type the marker entities are instances of.</summary>
    public static readonly RtCkId<CkTypeId> SensorReadingCkTypeId =
        new("MeshAdapterIntegrationTest/SensorReading");

    /// <summary>
    ///     🔴 A value that exists in tenant A's database and in no other. It must never be observed by
    ///     an execution of another tenant, whatever else the test asserts.
    /// </summary>
    public const string PoisonCanary = "POISON-CANARY-NEVER-CROSSES-A-TENANT-BOUNDARY";

    /// <summary>Every tenant this fixture creates.</summary>
    public static IReadOnlyList<string> Tenants { get; } = [TenantA, TenantB, TenantC];

    /// <summary>The marker only tenant <paramref name="tenantId" />'s database holds.</summary>
    public static string MarkerOf(string tenantId) => $"MARKER-OF-{tenantId.ToUpperInvariant()}";

    public TwoTenantLeaseFixture()
    {
        // Registered before the provider is built; the base constructor already added the runtime
        // engine and System v2.
        Services.AddCkModelMeshAdapterIntegrationTestV1();
    }

    protected override async Task InitializeServicesAsync()
    {
        // Builds the provider and creates the system tenant (which auto-imports the System model).
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var ckCacheService = GetService<ICkCacheService>();

        foreach (var tenantId in Tenants)
        {
            using var adminSession = await systemContext.GetAdminSessionAsync();
            if (await systemContext.IsChildTenantExistingAsync(adminSession, tenantId))
            {
                await systemContext.DropChildTenantAsync(adminSession, tenantId);
            }

            // A child of the system tenant — which is also the shape a real lending relationship
            // takes: a pool lends downwards into its own subtree (concept §3).
            await systemContext.CreateChildTenantAsync(adminSession, $"db{tenantId}", tenantId);

            var operationResult = new OperationResult();
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"),
                operationResult);

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw new InvalidOperationException(
                    $"Failed to import the test CK model into tenant '{tenantId}': " +
                    string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
            }

            await SeedMarkerAsync(systemContext, ckCacheService, tenantId);
        }

        // 🔴 Leave the caches cold. A fixture that left every tenant's model loaded would hide
        // exactly the state the post-release assertions are about.
        foreach (var tenantId in Tenants.Where(ckCacheService.IsTenantLoaded))
        {
            ckCacheService.Unload(tenantId);
        }
    }

    private static async Task SeedMarkerAsync(ISystemContext systemContext, ICkCacheService ckCacheService,
        string tenantId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var entities = new List<EntityUpdateInfo<RtEntity>>();

        var marker = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
        marker.SetAttributeRawValue("SerialNumber", MarkerOf(tenantId));
        entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(marker));

        if (tenantId == TenantA)
        {
            var canary = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
            canary.SetAttributeRawValue("SerialNumber", PoisonCanary);
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(canary));
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entities, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(
                $"Failed to seed the marker of tenant '{tenantId}': {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }
}

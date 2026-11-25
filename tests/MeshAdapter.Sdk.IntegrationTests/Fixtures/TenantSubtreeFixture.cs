using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     A real tenant tree in one real MongoDB for the cross-tenant guard:
///     <code>
///     system
///     ├── xtg            group ── xta ── xta1 (grandchild of the group)
///     │                        ├─ xtb
///     │                        └─ xtd (detached after seeding: database kept, registry row gone)
///     └── xth            other group ── xtx (outsider)
///     </code>
///     Every tenant holds one marker entity whose serial number names the tenant, so a test sees exactly whose data it
///     read. <see cref="TenantB" /> additionally holds a <see cref="PoisonCanary" /> that must never be returned to a
///     refused access.
/// </summary>
public class TenantSubtreeFixture : SystemFixture
{
    public const string Group = "xtg";
    public const string TenantA = "xta";
    public const string TenantB = "xtb";
    public const string GrandChild = "xta1";
    public const string Detached = "xtd";
    public const string OtherGroup = "xth";
    public const string Outsider = "xtx";

    public const string PoisonCanary = "POISON-CANARY-OF-B-NEVER-READ-WITHOUT-A-GRANT";

    public static readonly RtCkId<CkTypeId> SensorReadingCkTypeId = new("MeshAdapterIntegrationTest/SensorReading");

    public static string MarkerOf(string tenantId) => $"MARKER-OF-{tenantId.ToUpperInvariant()}";

    public TenantSubtreeFixture()
    {
        Services.AddCkModelMeshAdapterIntegrationTestV1();
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var ckCacheService = GetService<ICkCacheService>();

        await CreateBelowSystemAsync(systemContext, Group);
        await CreateBelowSystemAsync(systemContext, OtherGroup);
        await CreateBelowAsync(systemContext, Group, TenantA);
        await CreateBelowAsync(systemContext, Group, TenantB);
        await CreateBelowAsync(systemContext, Group, Detached);
        await CreateBelowAsync(systemContext, TenantA, GrandChild);
        await CreateBelowAsync(systemContext, OtherGroup, Outsider);

        foreach (var tenantId in new[] { Group, TenantA, TenantB, GrandChild, Detached, OtherGroup, Outsider })
        {
            await ImportModelAndSeedAsync(systemContext, ckCacheService, tenantId);
        }

        // Detach: the database and its data stay, the registry no longer knows the tenant.
        var groupContext = await systemContext.FindTenantContextAsync(Group);
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            await groupContext.DetachChildTenantAsync(adminSession, Detached);
        }
    }

    private static async Task CreateBelowSystemAsync(ISystemContext systemContext, string tenantId)
    {
        using var adminSession = await systemContext.GetAdminSessionAsync();
        if (await systemContext.IsChildTenantExistingAsync(adminSession, tenantId))
        {
            await systemContext.DropChildTenantAsync(adminSession, tenantId);
        }

        await systemContext.CreateChildTenantAsync(adminSession, $"db{tenantId}", tenantId);
    }

    protected static async Task CreateBelowAsync(ISystemContext systemContext, string parentTenantId, string tenantId)
    {
        var parentContext = await systemContext.FindTenantContextAsync(parentTenantId);
        using var adminSession = await systemContext.GetAdminSessionAsync();
        if (await parentContext.IsChildTenantExistingAsync(adminSession, tenantId))
        {
            await parentContext.DropChildTenantAsync(adminSession, tenantId);
        }

        await parentContext.CreateChildTenantAsync(adminSession, $"db{tenantId}", tenantId);
    }

    protected static async Task ImportModelAndSeedAsync(ISystemContext systemContext, ICkCacheService ckCacheService,
        string tenantId)
    {
        var operationResult = new OperationResult();
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
        await tenantContext.ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"), operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                $"Failed to import the test CK model into tenant '{tenantId}': {operationResult.GetMessages()}");
        }

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var entities = new List<EntityUpdateInfo<RtEntity>>();
        var marker = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
        marker.SetAttributeRawValue("SerialNumber", MarkerOf(tenantId));
        entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(marker));
        if (tenantId == TenantB)
        {
            var canary = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
            canary.SetAttributeRawValue("SerialNumber", PoisonCanary);
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(canary));
        }

        var seedResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entities, seedResult);
        if (seedResult.HasErrors || seedResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to seed tenant '{tenantId}': {seedResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }
}

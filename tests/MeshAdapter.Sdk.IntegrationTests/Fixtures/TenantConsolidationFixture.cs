using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     <see cref="TenantSubtreePipelineFixture" /> plus invoices for the consolidated read of a group (AB#9771):
///     <code>
///     xtg (group)
///     ├── xta   invoices 100 + 50
///     ├── xtb   invoice 70
///     ├── xtc   invoice 999 (the company the controller holds no right in)
///     ├── xtd   detached (no invoices, no registry row)
///     └── xta1  below xta: invoice 500 (a grandchild of the group)
///     </code>
///     A separate fixture (own MongoDB container) so that the extra child of the group does not change the children
///     the other cross-tenant tests see.
/// </summary>
public sealed class TenantConsolidationFixture : TenantSubtreePipelineFixture
{
    public const string TenantC = "xtc";

    public static readonly RtCkId<CkTypeId> InvoiceCkTypeId = new("MeshAdapterIntegrationTest/InterCompanyInvoice");

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var ckCacheService = GetService<ICkCacheService>();
        await CreateBelowAsync(systemContext, Group, TenantC);
        await ImportModelAndSeedAsync(systemContext, ckCacheService, TenantC);

        await SeedInvoicesAsync(systemContext, ckCacheService, TenantA, 100.0, 50.0);
        await SeedInvoicesAsync(systemContext, ckCacheService, TenantB, 70.0);
        await SeedInvoicesAsync(systemContext, ckCacheService, TenantC, 999.0);
        await SeedInvoicesAsync(systemContext, ckCacheService, GrandChild, 500.0);
    }

    private static async Task SeedInvoicesAsync(ISystemContext systemContext, ICkCacheService ckCacheService,
        string tenantId, params double[] amounts)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var entities = new List<EntityUpdateInfo<RtEntity>>();
        for (var i = 0; i < amounts.Length; i++)
        {
            var invoice = new RtEntity(InvoiceCkTypeId, OctoObjectId.GenerateNewId());
            invoice.SetAttributeRawValue("InvoiceNumber", $"INV-{tenantId.ToUpperInvariant()}-{i + 1}");
            invoice.SetAttributeRawValue("InvoiceAmount", amounts[i]);
            // Mandatory attributes of the type; a consolidation does not read them.
            invoice.SetAttributeRawValue("RecipientTenantId", Group);
            invoice.SetAttributeRawValue("InvoiceStatus", "Booked");
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(invoice));
        }

        var result = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entities, result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to seed invoices into '{tenantId}': {result.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }
}

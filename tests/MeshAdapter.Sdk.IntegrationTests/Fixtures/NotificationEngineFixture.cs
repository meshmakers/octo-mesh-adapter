using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     A child tenant holding the System.Notification model and the test model whose
///     <c>SensorReading</c> entities are the rows a notification query returns.
/// </summary>
public class NotificationEngineFixture : SystemFixture
{
    public const string TenantId = "notifyengine";

    public static readonly RtCkId<CkTypeId> SensorReadingCkTypeId = new("MeshAdapterIntegrationTest/SensorReading");

    public NotificationEngineFixture()
    {
        Services.AddCkModelMeshAdapterIntegrationTestV1();
        Services.AddCkModelSystemNotificationV2();
    }

    public ITenantRepository TenantRepository { get; private set; } = null!;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            if (await systemContext.IsChildTenantExistingAsync(adminSession, TenantId))
            {
                await systemContext.DropChildTenantAsync(adminSession, TenantId);
            }

            await systemContext.CreateChildTenantAsync(adminSession, $"db{TenantId}", TenantId);
        }

        // The embedded, versioned id: a bare model name finds nothing to import and reports no error.
        var tenantContext = await systemContext.FindTenantContextAsync(TenantId);
        foreach (var model in new[] { new CkModelId("MeshAdapterIntegrationTest"), SystemNotificationCkIds.CkModelId })
        {
            var operationResult = new OperationResult();
            await tenantContext.ImportCkModelAsync(model, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw new InvalidOperationException($"Failed to import the CK model '{model}': " +
                                                    string.Join(", ",
                                                        operationResult.Messages.Select(m => m.MessageText)));
            }
        }

        var ckCacheService = GetService<ICkCacheService>();
        if (ckCacheService.IsTenantLoaded(TenantId))
        {
            ckCacheService.Unload(TenantId);
        }

        TenantRepository = await systemContext.FindTenantRepositoryAsync(TenantId);
        await TenantRepository.LoadCacheForTenantAsync(ckCacheService);
        if (!ckCacheService.GetCkModelIds(TenantId).Contains(SystemNotificationCkIds.CkModelId))
        {
            throw new InvalidOperationException(
                $"{SystemNotificationCkIds.CkModelId} is not loaded for tenant '{TenantId}'");
        }
    }
}

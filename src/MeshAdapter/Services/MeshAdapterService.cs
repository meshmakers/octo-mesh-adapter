using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class MeshAdapterService(
    ILogger<MeshAdapterService> logger,
    IPipelineRegistryService pipelineRegistryService,
    ICkCacheService ckCacheService,
    IEventHubControl eventHubControl) : IAdapterService
{
    public async Task<bool> StartupAsync(AdapterStartup adapterStartup, List<DeploymentUpdateErrorMessageDto> errorMessages,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Startup of mesh adapter");
        try
        {
            // Clean the cache for the tenant to ensure no stale data is present
            logger.LogInformation("Unloading cache for tenant: {TenantId}", adapterStartup.TenantId);
            ckCacheService.Unload(adapterStartup.TenantId);

            // Register the adapter configuration
            logger.LogInformation("Registering adapter configuration for tenant: {TenantId}", adapterStartup.TenantId);
            var success =await pipelineRegistryService.RegisterPipelinesAsync(adapterStartup.TenantId,
                adapterStartup.Configuration.Pipelines, errorMessages);

            await eventHubControl.StartAsync(stoppingToken);
            
            return success;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while startup");
            throw;
        }
    }

    public async Task ShutdownAsync(AdapterShutdown adapterShutdown, CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Shutdown of mesh adapter");
            await eventHubControl.StopAsync(stoppingToken);
            await pipelineRegistryService.UnregisterAllPipelinesAsync(adapterShutdown.TenantId);
            logger.LogInformation("Mesh Adapter service stopped");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while shutdown");
            throw;
        }
    }
}
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
    public async Task<bool> StartupAsync(AdapterStartup adapterStartup,
        List<DeploymentUpdateErrorMessageDto> errorMessages,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Startup of mesh adapter (StartEventHub={StartEventHub})", adapterStartup.StartEventHub);
        try
        {
            // Clean the cache for the tenant to ensure no stale data is present
            logger.LogInformation("Unloading cache for tenant: {TenantId}", adapterStartup.TenantId);
            if (ckCacheService.IsTenantLoaded(adapterStartup.TenantId))
            {
                ckCacheService.Unload(adapterStartup.TenantId);
            }

            // Register the adapter configuration
            logger.LogInformation("Registering adapter configuration for tenant: {TenantId}", adapterStartup.TenantId);
            var success = await pipelineRegistryService.RegisterPipelinesAsync(adapterStartup.TenantId,
                adapterStartup.Configuration.Pipelines, errorMessages);

            if (adapterStartup.StartEventHub)
            {
                logger.LogInformation("Starting event hub for tenant: {TenantId}", adapterStartup.TenantId);
                await eventHubControl.StartAsync(stoppingToken);
            }
            else
            {
                logger.LogInformation("Skipping event hub start for tenant: {TenantId} (configuration update)",
                    adapterStartup.TenantId);
            }

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
            logger.LogInformation("Shutdown of mesh adapter (StopEventHub={StopEventHub})",
                adapterShutdown.StopEventHub);

            // Unregister pipelines first to stop trigger nodes before stopping the bus.
            // Trigger nodes (e.g. FromSchedule) fire on timers and send MassTransit messages.
            // If the bus is stopped first, it waits for in-flight consumers to complete,
            // but running triggers keep sending new messages, preventing the bus from stopping.
            await pipelineRegistryService.UnregisterAllPipelinesAsync(adapterShutdown.TenantId);

            if (adapterShutdown.StopEventHub)
            {
                logger.LogInformation("Stopping event hub for tenant: {TenantId}", adapterShutdown.TenantId);
                await eventHubControl.StopAsync(stoppingToken);
            }
            else
            {
                logger.LogInformation("Skipping event hub stop for tenant: {TenantId} (configuration update)",
                    adapterShutdown.TenantId);
            }

            logger.LogInformation("Mesh Adapter service stopped");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while shutdown");
            throw;
        }
    }
}
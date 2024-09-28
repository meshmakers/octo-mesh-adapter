using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class MeshAdapterService(
    ILogger<MeshAdapterService> logger,
    IPipelineRegistryService pipelineRegistryService,
    IEventHubControl eventHubControl) : IAdapterService
{
    public async Task StartupAsync(AdapterStartup adapterStartup, CancellationToken stoppingToken)
    {
        logger.LogInformation("Startup of mesh adapter");
        try
        {
            await pipelineRegistryService.RegisterPipelinesAsync(adapterStartup.TenantId,
                adapterStartup.Configuration.Pipelines);
            await pipelineRegistryService.StartTriggerPipelineNodesAsync(adapterStartup.TenantId);

            await eventHubControl.StartAsync(stoppingToken);
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
            await pipelineRegistryService.StopTriggerPipelineNodesAsync(adapterShutdown.TenantId);
            
            pipelineRegistryService.UnregisterAllPipelines(adapterShutdown.TenantId);
            await eventHubControl.StopAsync(stoppingToken);
            logger.LogInformation("Mesh Adapter service stopped");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while shutdown");
            throw;
        }
    }
}
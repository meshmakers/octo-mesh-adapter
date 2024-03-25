using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.Sdk.Common.Adapters;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class MeshAdapterService(ILogger<MeshAdapterService> logger, IMeshPipelineExecutionService pipelineExecutionService, IEventHubControl eventHubControl) : IAdapterService
{
    public async Task StartupAsync(AdapterStartup adapterStartup, CancellationToken stoppingToken)
    {
        logger.LogInformation("Startup of mesh adapter");
        try
        {
            foreach (var dataPipelineConfiguration in adapterStartup.Configuration.Pipelines)
            {
                await pipelineExecutionService.RegisterPipeline(adapterStartup.TenantId, dataPipelineConfiguration);
            }

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
            pipelineExecutionService.UnregisterAllPipelines(adapterShutdown.TenantId);
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
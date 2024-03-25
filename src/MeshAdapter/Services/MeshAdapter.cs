using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;
using NLog;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class MeshAdapter(IMeshPipelineExecutionService pipelineExecutionService) : IAdapterService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task StartupAsync(AdapterStartup adapterStartup, CancellationToken stoppingToken)
    {
        try
        {
            foreach (var dataPipelineConfiguration in adapterStartup.Configuration.Pipelines)
            {
                await pipelineExecutionService.RegisterPipeline(adapterStartup.TenantId, dataPipelineConfiguration);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error while startup");
            throw;
        }
      
    }

    public Task ShutdownAsync(AdapterShutdown adapterShutdown, CancellationToken stoppingToken)
    {
        try
        {
            pipelineExecutionService.UnregisterAllPipelines(adapterShutdown.TenantId);
            Logger.Info("Mesh Adapter service stopped");
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error while shutdown");
            throw;
        }
    }
}
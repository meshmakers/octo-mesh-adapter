using Meshmakers.Octo.Sdk.Common.Adapters;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class MeshAdapter : IAdapterService
{
    public Task StartupAsync(AdapterStartup adapterStartup, CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(AdapterShutdown adapterShutdown, CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }
}
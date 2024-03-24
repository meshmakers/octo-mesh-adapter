using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Repositories;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class RetrieverManager(IPipelineConfigurationRepository configurationRepository, 
    IRetrieverPipelineExecutionService pipelineExecutionService) : IRetrieverManager
{

    public async Task LoadAsync(string tenantId)
    {
        var configurations = await configurationRepository.GetRetrieverConfigurationsAsync(tenantId);
        foreach (var configuration in configurations)
        {
            await pipelineExecutionService.RegisterPipeline(tenantId, configuration);
        }
    }

    public async Task UpdateAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var configuration = await configurationRepository.GetRetrieverConfigurationAsync(tenantId, pipelineRtId);
        pipelineExecutionService.UpdatePipeline(tenantId, configuration);
    }
}
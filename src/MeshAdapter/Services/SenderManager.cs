using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Repositories;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal class SenderManager(IPipelineConfigurationRepository configurationRepository, 
    ISenderPipelineExecutionService pipelineExecutionService) : ISenderManager
{
    public async Task LoadAsync(string tenantId)
    {
        var configurations = await configurationRepository.GetSenderConfigurationsAsync(tenantId);
        foreach (var configuration in configurations)
        {
            await pipelineExecutionService.RegisterPipeline(tenantId, configuration);
        }
    }

    public async Task UpdateAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var configuration = await configurationRepository.GetSenderConfigurationAsync(tenantId, pipelineRtId);
        pipelineExecutionService.UpdatePipeline(tenantId, configuration);
    }
}
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.MeshAdapter.Consumers;

/// <summary>
/// Consumer for PipelineTriggerSchedule using distributed event hub
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class PipelineTriggerScheduleConsumer(
    ILogger<PipelineTriggerScheduleConsumer> logger,
    IMeshPipelineExecutionService pipelineExecutionService)
    : IDistributedConsumer<PipelineTriggerSchedule>
{
    public async Task ConsumeAsync(IDistributedContext<PipelineTriggerSchedule> context)
    {
        var message = context.Message;
        logger.LogInformation("[{TenantId}] Received", message.TenantId);

        foreach (var pipelineRtId in context.Message.PipelineRtIdList)
        {
            try
            {
                await pipelineExecutionService.ExecutePipelineAsync(context.Message.TenantId.NormalizeString(),
                    new RtEntityId("System.Communication/MeshPipeline", pipelineRtId), new ExecutePipelineOptions(DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{TenantId}] Error processing pipeline: '{PipelineId}'",
                    message.TenantId, pipelineRtId);
                throw;
            }
        }
    }
}
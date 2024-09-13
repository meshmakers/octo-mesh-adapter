using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

namespace Meshmakers.Octo.MeshAdapter.Consumers;

/// <summary>
/// Consumer that executes a mesh pipeline
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class ExecuteMeshPipelineConsumer(
    ILogger<ExecuteMeshPipelineConsumer> logger,
    IMeshPipelineExecutionService pipelineExecutionService) :
    IDistributedConsumer<ExecuteMeshPipelineRequest>
{
    public async Task ConsumeAsync(IDistributedContext<ExecuteMeshPipelineRequest> context)
    {
        var message = context.Message;
        logger.LogInformation("[{TenantId}] Received", message.TenantId);

        try
        {
            var output = await pipelineExecutionService.ExecutePipelineAsync(context.Message.TenantId.NormalizeString(),
                context.Message.MeshPipelineRtEntityId, new ExecutePipelineOptions(DateTime.UtcNow),
                context.Message.PipelineInput);

            var result = output?.Serialize() ?? null;
            
            await context.RespondAsync(new ExecuteMeshPipelineResponse(true, null, result));
        }
        catch (Exception ex)
        {
            await context.RespondAsync(new ExecuteMeshPipelineResponse(false, ex.Message, null));

            logger.LogError(ex, "[{TenantId}] Error processing pipeline: '{PipelineId}'",
                message.TenantId, context.Message.MeshPipelineRtEntityId);
            throw;
        }
    }
}
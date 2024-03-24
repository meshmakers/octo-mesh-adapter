using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Consumers;

/// <summary>
/// Consumer for PipelineDataReceived using distributed event hub
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class PipelineDataReceivedConsumer(
    ILogger<PipelineDataReceivedConsumer> logger,
    IRetrieverPipelineExecutionService retrieverPipelineExecutionService)
    : IDistributedConsumer<PipelineDataReceived>
{
    public async Task ConsumeAsync(IDistributedContext<PipelineDataReceived> context)
    {
        var message = context.Message;
        logger.LogInformation("[{TenantId}] Received Input: PipelineId '{PipelineId}', Value '{Value}'",
            message.TenantId, message.DataPipelineRtId, message.Value);

        try
        {
            await retrieverPipelineExecutionService.ExecutePipelineAsync(context.Message.TenantId.NormalizeString(),
                context.Message.DataPipelineRtId,
                new ExecutePipelineOptions(context.Message.TransactionStartedDateTime, SendDebugInfo)
                    { ExternalReceivedDateTime = context.Message.ExternalReceivedDateTime }, context.Message.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{TenantId}] Error processing pipeline: '{PipelineId}', Value '{Value}'",
                message.TenantId, message.DataPipelineRtId, message.Value);
            throw;
        }
    }

    private Task SendDebugInfo(OctoObjectId pipelineRtId, string debugInfo)
    {
        return Task.CompletedTask;
    }
}
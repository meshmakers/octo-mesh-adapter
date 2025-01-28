using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;

[NodeConfiguration(typeof(FromSendNotificationNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromSendNotificationNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"{QueueNames.SendNotificationCommand.ToLower()}-{context.TenantId.ToLower()}-data-pipeline-{context.DataPipelineRtId.ToString()?.ToLower()}";

        _endpointHandle = eventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(address,
            async (message, responseFunc) =>
            {
                try
                {
                    context.NodeContext.Info("Received command send notification");

                    JToken input = JArray.FromObject(message.Notifications);

                    var pipelineExecutionId =
                        await context.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow), input);
                    await responseFunc(new ExecuteMeshPipelineResponse(true, null, pipelineExecutionId));
                }
                catch (Exception ex)
                {
                    await responseFunc(new ExecuteMeshPipelineResponse(false, ex.Message, null));

                    context.NodeContext.Error(ex, "[{TenantId}] Error processing pipeline: '{PipelineId}'",
                        message.TenantId, context.PipelineRtEntityId);
                    throw;
                }
            });

        return Task.CompletedTask;
    }

    public async Task StopAsync(ITriggerContext context)
    {
        if (_endpointHandle != null)
        {
            await _endpointHandle.DisposeAsync();
        }
    }
}
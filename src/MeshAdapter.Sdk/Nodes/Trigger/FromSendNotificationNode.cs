using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromSendNotificationNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromSendNotificationNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"{QueueNames.SendNotificationCommand.ToLower()}-{context.TenantId.ToLower()}-data-flow-{context.DataFlowRtId.ToString()?.ToLower()}";

        _endpointHandle = eventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(address,
            async (message, responseFunc) =>
            {
                try
                {
                    context.NodeContext.Info("Received command send notification");

                    JsonNode input = JsonSerializer.SerializeToNode(message.Notifications, SystemTextJsonOptions.Default) ?? new JsonArray();

                    var startDateTime = DateTime.UtcNow;
                    var pipelineExecutionId =
                        await context.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow), input);
                    await responseFunc(new ExecutePipelineResponse(true, null, pipelineExecutionId, startDateTime));

                    // Wait for pipeline completion and report execution end to communication controller
                    await context.EndExecutePipelineAsync(pipelineExecutionId);
                }
                catch (Exception ex)
                {
                    await responseFunc(new ExecutePipelineResponse(false, ex.Message, null, null));

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
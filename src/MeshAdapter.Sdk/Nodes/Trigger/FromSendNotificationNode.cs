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
    /// <summary>
    /// Why the send-notification command consumer stays exclusive (AB#8405, option from AB#8363). Logged by the
    /// event hub at registration (Debug) and listed in the octo-distributedEventHub CLAUDE.md audit table.
    /// </summary>
    internal const string ExclusiveByDesignReason =
        "one endpoint per data flow (send-notification-pipeline-{tenant}-data-flow-{dataFlowRtId}); no sender " +
        "addresses it: identity-services and common-services NotificationService publish SendNotificationsRequest and " +
        "the classic queue receives it through its message-type binding (MassTransit default consume topology), so a " +
        "further adapter replica is a hot standby; the shared topology turns that binding off and belongs with a " +
        "migration of the publishers (AB#8405)";

    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"{QueueNames.SendNotificationCommand.ToLower()}-{context.TenantId.ToLower()}-data-flow-{context.DataFlowRtId.ToString()?.ToLower()}";

        // Exclusive by design (AB#8405): exactly one process owns this data flow's notification endpoint. The
        // notifications arrive by publish (DistributionEventHubService.PublishAsync) through the message-type binding
        // of the classic queue, which exclusive-by-design keeps; the plain shared options would drop that binding and
        // the endpoint would receive nothing. Same queue and wire behaviour as before; the declaration only replaces
        // the AB#6909 deprecation warning with a stated decision.
        _endpointHandle = eventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(address,
            async (message, responseFunc) =>
            {
                // Tenant guard (AB#8409): the request is published to the message-type exchange, so this queue receives
                // the notifications of every tenant on the broker (OTP codes, reset links). Drop foreign ones before
                // anything else: no pipeline start, no response; returning normally acknowledges the message. The log
                // names the two tenant ids only, never recipients, subject or body. Routing per tenant: AB#8410.
                if (!IsOwnTenant(message, context))
                {
                    context.NodeContext.Warning(
                        "Dropped send-notification request of tenant '{MessageTenantId}' received by the pipeline of tenant '{PipelineTenantId}'",
                        message.TenantId, context.TenantId);
                    return;
                }

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
            },
            CommandConsumerOptions.ExclusiveByDesign(ExclusiveByDesignReason));

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the request belongs to the tenant of this pipeline (case-insensitive, tenant ids are compared the way
    /// the endpoint address lower-cases them).
    /// </summary>
    internal static bool IsOwnTenant(SendNotificationsRequest message, ITriggerContext context) =>
        string.Equals(message.TenantId, context.TenantId, StringComparison.OrdinalIgnoreCase);

    public async Task StopAsync(ITriggerContext context)
    {
        if (_endpointHandle != null)
        {
            await _endpointHandle.DisposeAsync();
        }
    }
}
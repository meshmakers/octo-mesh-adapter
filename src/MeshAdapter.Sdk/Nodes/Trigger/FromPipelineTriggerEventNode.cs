using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Cron / scheduled trigger. The communication controller registers one recurring send per
/// (trigger, pipeline) whose ticks land in the durable queue
/// <c>octo::bot::pipeline-trigger-{tenant}-{pipelineRtId}</c>; this node consumes that queue.
/// </summary>
/// <remarks>
/// AB#5709: the queue keeps buffering while the adapter is down or hibernated (OnDemand, AB#4918), and
/// the consumer used to take the whole backlog at MassTransit's default prefetch — 20 parallel runs of an
/// hourly pipeline after a 20 h outage. The consumer is now registered latest-only (prefetch 1,
/// concurrency 1, backlog coalesced into the newest tick) unless
/// <see cref="FromPipelineTriggerEventNodeConfiguration.AllowConcurrentExecution" /> opts out. Purely
/// consumer side: the queue keeps its declaration, so no queue migration is needed.
/// </remarks>
[NodeConfiguration(typeof(FromPipelineTriggerEventNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromPipelineTriggerEventNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"{QueueNames.PipelineTriggerChannelName.ToLower()}-{context.TenantId.ToLower()}-{context.PipelineRtEntityId.RtId.ToString().ToLower()}";

        var configuration = context.NodeContext.GetNodeConfiguration<FromPipelineTriggerEventNodeConfiguration>();
        var options = ResolveConsumerOptions(configuration);

        _endpointHandle = eventHubControl.RegisterRoutedEventConsumer<PipelineTriggerSchedule>(address,
            async (message, delivery) =>
            {
                if (delivery.CoalescedMessageCount > 0)
                {
                    context.NodeContext.Info(
                        "[{TenantId}] Received; {CoalescedTicks} older pending tick(s) coalesced into this run",
                        message.TenantId, delivery.CoalescedMessageCount);
                }
                else
                {
                    context.NodeContext.Info("[{TenantId}] Received", message.TenantId);
                }

                try
                {
                    await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    context.NodeContext.Error(ex,
                        "[{TenantId}] Pipeline execution failed for trigger event", message.TenantId);
                }
            }, options);

        return Task.CompletedTask;
    }

    public async Task StopAsync(ITriggerContext context)
    {
        if (_endpointHandle != null)
        {
            await _endpointHandle.DisposeAsync();
        }
    }

    /// <summary>
    /// Latest-only unless the pipeline explicitly allows overlapping runs. A missing configuration
    /// (older definitions) gets the safe default.
    /// </summary>
    internal static RoutedEventConsumerOptions ResolveConsumerOptions(
        FromPipelineTriggerEventNodeConfiguration? configuration)
    {
        return configuration?.AllowConcurrentExecution == true
            ? RoutedEventConsumerOptions.Default
            : RoutedEventConsumerOptions.LatestOnly;
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromNotificationScheduleNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromNotificationScheduleNode(IEventHubControl eventHubControl) : ITriggerPipelineNode
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        _endpointHandle = eventHubControl.RegisterRoutedEventConsumer<NotificationScheduleTick>(
            CreateAddress(context.TenantId, context.PipelineRtEntityId.RtId.ToString()),
            async message =>
            {
                if (string.IsNullOrWhiteSpace(message.PreferenceRtId))
                {
                    context.NodeContext.Warning("[{TenantId}] Notification tick without a preference; ignored",
                        message.TenantId);
                    return;
                }

                if (!_running.TryAdd(message.PreferenceRtId, 0))
                {
                    context.NodeContext.Debug(
                        "[{TenantId}] Preference '{PreferenceRtId}' is still running; this tick is skipped",
                        message.TenantId, message.PreferenceRtId);
                    return;
                }

                context.NodeContext.Debug("[{TenantId}] Notification tick for preference '{PreferenceRtId}'",
                    message.TenantId, message.PreferenceRtId);

                try
                {
                    await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), new JsonObject
                    {
                        ["tenantId"] = message.TenantId,
                        ["preferenceRtId"] = message.PreferenceRtId
                    });
                }
                catch (Exception ex)
                {
                    context.NodeContext.Warning(
                        "[{TenantId}] Notification run failed for preference '{PreferenceRtId}': {Error}",
                        message.TenantId, message.PreferenceRtId, ex.Message);
                }
                finally
                {
                    _running.TryRemove(message.PreferenceRtId, out _);
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

    internal static string CreateAddress(string tenantId, string pipelineRtId) =>
        $"{QueueNames.NotificationScheduleChannelName.ToLower()}-{tenantId.ToLower()}-{pipelineRtId.ToLower()}";
}

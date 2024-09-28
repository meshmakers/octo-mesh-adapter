using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;


[NodeConfiguration(typeof(FromPipelineTriggerEventNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromPipelineTriggerEventNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"data-pipeline-{context.TenantId.ToLower()}-{context.DataPipelineRtId.ToString().ToLower()}-{nameof(PipelineTriggerSchedule).ToLower()}";

        _endpointHandle = eventHubControl.RegisterRoutedEventConsumer<PipelineTriggerSchedule>(address,
            async message =>
            {
                context.NodeContext.Info("[{TenantId}] Received", message.TenantId);
                
                await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow));
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

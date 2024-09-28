using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;

[NodeConfiguration(typeof(FromPipelineDataEventNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromPipelineDataEventNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"data-pipeline-{context.TenantId.ToLower()}-{context.DataPipelineRtId.ToString()?.ToLower()}-{nameof(PipelineDataReceived).ToLower()}";

        _endpointHandle = eventHubControl.RegisterRoutedEventConsumer<PipelineDataReceived>(address,
            async message =>
            {
                if (message.Value == null)
                {
                    context.NodeContext.Warning("Received message with null value");
                    return;
                }

                var input = JToken.Parse(message.Value);
                await context.ExecuteAsync(new ExecutePipelineOptions(message.TransactionStartedDateTime)
                    { ExternalReceivedDateTime = message.ExternalReceivedDateTime }, input);
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
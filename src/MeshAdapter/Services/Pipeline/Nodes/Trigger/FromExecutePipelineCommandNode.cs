using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;

[NodeConfiguration(typeof(FromExecutePipelineCommandNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromExecutePipelineCommandNode(IEventHubControl eventHubControl)
    : ITriggerPipelineNode
{
    private EndpointHandle? _endpointHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var address =
            $"{QueueNames.ExecuteMeshPipelineCommand.ToLower()}-{context.TenantId.ToLower()}-data-pipeline-{context.DataPipelineRtId.ToString()?.ToLower()}";

        _endpointHandle = eventHubControl.RegisterCommandConsumer<ExecuteMeshPipelineRequest>(address,
            async (message, responseFunc) =>
            {
                try
                {
                    context.NodeContext.Info("Received command executing pipeline");
                    
                    JToken input = JValue.CreateNull();
                    if (message.PipelineInput != null)
                    {
                        input = JToken.Parse(message.PipelineInput);
                    }

                    var pipelineExecutionId = await context.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow), input);
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
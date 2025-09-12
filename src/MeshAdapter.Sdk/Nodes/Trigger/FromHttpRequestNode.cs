using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromHttpRequestNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromHttpRequestNode(ILogger<FromHttpRequestNode> logger, IHttpRequestService httpRequestService)
    : ITriggerPipelineNode
{
    private HttpRouteHandle? _routeHandle;

    public Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration>();

        var requestOptions = new HttpRequestOptions(c.Path, c.Method, async input =>
        {
            try
            {
                var result = await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), input);
                if (result == null)
                {
                    return null;
                }

                return JToken.FromObject(result);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to execute pipeline");

                // Ensure we return an error response
                throw;
            }
        });
        _routeHandle = httpRequestService.CreateRoute(requestOptions);

        return Task.CompletedTask;
    }

    public Task StopAsync(ITriggerContext context)
    {
        _routeHandle?.Dispose();
        return Task.CompletedTask;
    }
}
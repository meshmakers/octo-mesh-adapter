using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.Extensions.Logging;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromHttpRequestNodeConfiguration2))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromHttpRequestNode2(
    ILogger<FromHttpRequestNode2> logger,
    IHttpRequestService httpRequestService,
    IAdapterEventService eventService)
    : ITriggerPipelineNode
{
    private HttpRouteHandle? _routeHandle;

    public async Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration2>();

        var requestOptions = new HttpRequestOptions(c.Path, c.Method, async input =>
        {
            try
            {
                var result = await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), input);
                if (result == null)
                {
                    return null;
                }

                return JsonSerializer.SerializeToNode(result, SystemTextJsonOptions.Default);
            }
            catch (RuntimeRepositoryException ex)
            {
                var messages = ex.OperationResult.GetMessages();
                var o = new OperationFailedErrorDto(ex.Message,
                    [new FailedDetailsDto { Code = null, Description = messages }]);
                return JsonSerializer.SerializeToNode(o, SystemTextJsonOptions.Default);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to execute pipeline");

                // Ensure we return an error response
                throw;
            }
        }, c.AllowAnonymous, c.RequiredRoles);
        _routeHandle = httpRequestService.CreateRoute(requestOptions);

        // An unauthenticated route is a standing exposure, not a per-request occurrence, so the
        // fact that one exists is audited once per deployment - where an operator can find it.
        if (c.AllowAnonymous)
        {
            await eventService.StoreInformationEventAsync(context.TenantId,
                $"Route {c.Method.ToString().ToUpper()} {c.Path} registered without authentication.");
        }
    }

    public Task StopAsync(ITriggerContext context)
    {
        _routeHandle?.Dispose();
        return Task.CompletedTask;
    }
}

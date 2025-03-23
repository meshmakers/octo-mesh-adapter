using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Middlewares;

internal class DynamicRouteMiddleware(RequestDelegate next, IHttpRequestService httpRequestService)
{
    public async Task Invoke(HttpContext context)
    {
        if (await httpRequestService.SendRequestAsync(context))
        {
            return;
        }

        await next(context); 
    }

}
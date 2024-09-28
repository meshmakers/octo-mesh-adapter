using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.HttpRequests;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.MeshAdapter.Middlewares;

internal class DynamicRouteMiddleware(RequestDelegate next, IHttpRequestService httpRequestService)
{
    public async Task Invoke(HttpContext context)
    {
        if (await httpRequestService.SendRequestAsync(context))
        {
            return;
        }

        await next(context); // Nächste Middleware aufrufen
    }

}
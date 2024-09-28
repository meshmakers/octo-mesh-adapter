namespace Meshmakers.Octo.MeshAdapter.Services.HttpRequests;

internal class HttpRouteHandle(HttpRequestService httpRequestService, HttpRequestOptions options) : IDisposable
{
    public void Dispose()
    {
        httpRequestService.RemoveRoute(options.Method, options.Route);
    }
}
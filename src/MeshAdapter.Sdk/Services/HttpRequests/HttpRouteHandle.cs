namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal class HttpRouteHandle(IHttpRequestService httpRequestService, HttpRequestOptions options) : IDisposable
{
    public void Dispose()
    {
        httpRequestService.RemoveRoute(options.Method, options.Route);
    }
}
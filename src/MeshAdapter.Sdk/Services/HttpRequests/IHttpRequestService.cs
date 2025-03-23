namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal interface IHttpRequestService
{
    HttpRouteHandle CreateRoute(HttpRequestOptions options);
    void RemoveRoute(Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod method, string uri);
    Task<bool> SendRequestAsync(Microsoft.AspNetCore.Http.HttpContext context);
}
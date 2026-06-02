using System.Text.Json.Nodes;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal class HttpRequestOptions(string route, HttpMethod method, Func<JsonNode, Task<JsonNode?>> executeFunc)
{
    public string Route { get; } = route;
    public HttpMethod Method { get; } = method;

    public Func<JsonNode, Task<JsonNode?>> ExecuteFunc { get; } = executeFunc;
}
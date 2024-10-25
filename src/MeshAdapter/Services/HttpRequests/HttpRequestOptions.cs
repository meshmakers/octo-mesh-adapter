using Newtonsoft.Json.Linq;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace Meshmakers.Octo.MeshAdapter.Services.HttpRequests;

internal class HttpRequestOptions(string route, HttpMethod method, Func<JToken, Task<JToken?>> executeFunc)
{
    public string Route { get; } = route;
    public HttpMethod Method { get; } = method;
    
    public Func<JToken, Task<JToken?>> ExecuteFunc{ get; } = executeFunc;
}
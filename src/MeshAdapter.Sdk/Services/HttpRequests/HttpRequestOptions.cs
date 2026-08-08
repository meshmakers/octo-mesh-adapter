using System.Text.Json.Nodes;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal class HttpRequestOptions(
    string route,
    HttpMethod method,
    Func<JsonNode, Task<JsonNode?>> executeFunc,
    bool allowAnonymous,
    string[] requiredRoles,
    bool receivesCredentialHeaders = false)
{
    public string Route { get; } = route;
    public HttpMethod Method { get; } = method;

    public Func<JsonNode, Task<JsonNode?>> ExecuteFunc { get; } = executeFunc;

    public bool AllowAnonymous { get; } = allowAnonymous;

    public string[] RequiredRoles { get; } = requiredRoles;

    /// <summary>
    /// Independent of <see cref="AllowAnonymous" />: that one decides whether the caller must
    /// authenticate, this one whether the pipeline gets to see the credential.
    /// </summary>
    public bool ReceivesCredentialHeaders { get; } = receivesCredentialHeaders;
}
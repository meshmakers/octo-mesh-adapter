using System.Text;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace MeshAdapter.Sdk.Tests.Services.HttpRequests;

public class HttpRequestServiceTests
{
    private const string TenantId = "testTenant";
    private readonly HttpRequestService _service;

    public HttpRequestServiceTests()
    {
        var options = Options.Create(new AdapterOptions { TenantId = TenantId });
        _service = new HttpRequestService(options);
    }

    #region CreateRoute

    [Fact]
    public void CreateRoute_ReturnsHandle()
    {
        var options = CreateRouteOptions("/test", HttpMethod.Get);

        var handle = _service.CreateRoute(options);

        Assert.NotNull(handle);
    }

    [Fact]
    public void CreateRoute_DuplicateRoute_ThrowsHttpRequestException()
    {
        var options = CreateRouteOptions("/test", HttpMethod.Post);
        _service.CreateRoute(options);

        Assert.Throws<Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestException>(
            () => _service.CreateRoute(options));
    }

    #endregion

    #region RemoveRoute

    [Fact]
    public void RemoveRoute_RouteRemoved_CanReregister()
    {
        var options = CreateRouteOptions("/test", HttpMethod.Get);
        _service.CreateRoute(options);

        _service.RemoveRoute(HttpMethod.Get, "/test");

        var handle = _service.CreateRoute(options);
        Assert.NotNull(handle);
    }

    #endregion

    #region HttpRouteHandle.Dispose

    [Fact]
    public void HttpRouteHandle_Dispose_RemovesRoute()
    {
        var options = CreateRouteOptions("/dispose-test", HttpMethod.Get);
        var handle = _service.CreateRoute(options);

        handle.Dispose();

        var newHandle = _service.CreateRoute(options);
        Assert.NotNull(newHandle);
    }

    #endregion

    #region SendRequestAsync

    [Fact]
    public async Task SendRequestAsync_KnownRoute_ExecutesFuncAndReturnsTrue()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/data", HttpMethod.Get, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(new JsonObject { ["status"] = "ok" });
        });
        _service.CreateRoute(options);

        var context = CreateHttpContext("GET", $"/{TenantId}/api/data");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.NotNull(receivedInput);
    }

    [Fact]
    public async Task SendRequestAsync_UnknownRoute_ReturnsFalse()
    {
        var context = CreateHttpContext("GET", $"/{TenantId}/unknown");
        var result = await _service.SendRequestAsync(context);

        Assert.False(result);
    }

    [Fact]
    public async Task SendRequestAsync_JsonBody_ParsedAsJsonNode()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/json", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        });
        _service.CreateRoute(options);

        var json = "{\"name\":\"test\",\"value\":42}";
        var context = CreateHttpContext("POST", $"/{TenantId}/api/json", json, "application/json");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.NotNull(receivedInput);
        var body = receivedInput!["body"];
        Assert.NotNull(body);
        Assert.Equal("test", body!["name"]?.ToString());
        Assert.Equal(42, body["value"]?.GetValue<int>());
    }

    [Fact]
    public async Task SendRequestAsync_TextBody_ParsedAsString()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/text", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        });
        _service.CreateRoute(options);

        const string textBody = "Hello, World!";
        var context = CreateHttpContext("POST", $"/{TenantId}/api/text", textBody, "text/plain");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.NotNull(receivedInput);
        Assert.Equal(textBody, receivedInput!["body"]?.ToString());
    }

    [Fact]
    public async Task SendRequestAsync_QueryParameters_SetInInput()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/query", HttpMethod.Get, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        });
        _service.CreateRoute(options);

        var context = CreateHttpContext("GET", $"/{TenantId}/api/query");
        context.Request.QueryString = new QueryString("?foo=bar&count=5");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.NotNull(receivedInput);
        var query = receivedInput!["query"];
        Assert.NotNull(query);
        Assert.Equal("bar", query!["foo"]?.ToString());
        Assert.Equal("5", query["count"]?.ToString());
    }

    [Fact]
    public async Task SendRequestAsync_ResponseWrittenAsJson()
    {
        var responseData = new JsonObject { ["result"] = "success" };
        var options = CreateRouteOptions("/api/respond", HttpMethod.Get, _ =>
            Task.FromResult<JsonNode?>(responseData));
        _service.CreateRoute(options);

        var context = CreateHttpContext("GET", $"/{TenantId}/api/respond");
        context.Response.Body = new MemoryStream();

        await _service.SendRequestAsync(context);

        Assert.Equal("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        var parsed = JsonNode.Parse(responseBody)!.AsObject();
        Assert.Equal("success", parsed["result"]?.ToString());
    }

    [Fact]
    public async Task SendRequestAsync_NullResponse_DoesNotWriteBody()
    {
        var options = CreateRouteOptions("/api/null", HttpMethod.Get, _ =>
            Task.FromResult<JsonNode?>(null));
        _service.CreateRoute(options);

        var context = CreateHttpContext("GET", $"/{TenantId}/api/null");
        context.Response.Body = new MemoryStream();

        await _service.SendRequestAsync(context);

        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task SendRequestAsync_PathAndMethodSetInInput()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/info", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        });
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/info");
        await _service.SendRequestAsync(context);

        Assert.NotNull(receivedInput);
        Assert.Equal($"/{TenantId}/api/info".ToLower(), receivedInput!["path"]?.ToString());
        Assert.Equal("POST", receivedInput["method"]?.ToString());
    }

    #endregion

    #region Helpers

    private static HttpRequestOptions CreateRouteOptions(string route, HttpMethod method,
        Func<JsonNode, Task<JsonNode?>>? executeFunc = null)
    {
        executeFunc ??= _ => Task.FromResult<JsonNode?>(null);
        return new HttpRequestOptions(route, method, executeFunc);
    }

    private static DefaultHttpContext CreateHttpContext(string method, string path,
        string? body = null, string? contentType = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = contentType;
        }

        return context;
    }

    #endregion
}

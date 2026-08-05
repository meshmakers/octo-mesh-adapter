using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using IdentityModel;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace MeshAdapter.Sdk.Tests.Services.HttpRequests;

public class HttpRequestServiceTests
{
    private const string TenantId = "testTenant";
    private const string TenantIdClaim = "tenant_id";
    private readonly HttpRequestService _service;
    private readonly RecordingAdapterEventService _eventService = new();

    public HttpRequestServiceTests()
    {
        _service = CreateService(auditAnonymousInvocations: true);
    }

    private HttpRequestService CreateService(bool auditAnonymousInvocations)
    {
        return new HttpRequestService(
            Options.Create(new AdapterOptions { TenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration
            {
                AuditAnonymousInvocations = auditAnonymousInvocations
            }),
            _eventService, NullLogger<HttpRequestService>.Instance);
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

    #region Authorization

    [Fact]
    public async Task SendRequestAsync_SecuredRouteWithoutToken_ReturnsUnauthorizedAndDoesNotExecute()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/secured", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/secured");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(executed);
    }

    [Fact]
    public async Task SendRequestAsync_SecuredRouteWithAuthenticatedCaller_ExecutesFunc()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/secured", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/secured");
        context.User = CreateAuthenticatedUser();
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
    }

    [Fact]
    public async Task SendRequestAsync_NoRequiredRolesConfigured_AcceptsAnyValidTokenWithoutRoleCheck()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/no-roles", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/no-roles");
        context.User = CreateAuthenticatedUser();
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
    }

    [Fact]
    public async Task SendRequestAsync_RequiredRoleMissing_ReturnsForbiddenAndDoesNotExecute()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/roles", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["TenantAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/roles");
        context.User = CreateAuthenticatedUser("Reader");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(executed);
    }

    [Fact]
    public async Task SendRequestAsync_AnyRequiredRoleHeld_ExecutesFunc()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/roles", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["TenantAdmin", "CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/roles");
        context.User = CreateAuthenticatedUser("CommunicationAdmin");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
    }

    [Fact]
    public async Task SendRequestAsync_BlankRequiredRole_ReturnsForbiddenInsteadOfThrowing()
    {
        var options = CreateRouteOptions("/api/blank-role", HttpMethod.Post,
            allowAnonymous: false, requiredRoles: [" "]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/blank-role");
        context.User = CreateAuthenticatedUser("TenantAdmin");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task SendRequestAsync_TokenOfAnotherTenant_ReturnsForbiddenAndDoesNotExecute()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/tenant", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/tenant");
        context.User = CreateAuthenticatedUserOfTenant("otherTenant");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(executed);
    }

    [Fact]
    public async Task SendRequestAsync_UserTokenWithoutTenant_ReturnsForbidden()
    {
        var options = CreateRouteOptions("/api/tenant", HttpMethod.Post, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/tenant");
        context.User = CreateAuthenticatedUserOfTenant(null);
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <remarks>
    /// A client credentials token carries neither a subject nor a tenant claim (AB#4183 injects
    /// only roles), so the tenant comparison must not apply to machine callers.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_MachineTokenWithoutSubject_IsAuthorizedByRoleAlone()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/machine", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtClaimTypes.Role, "CommunicationAdmin")],
            JwtBearerDefaults.AuthenticationScheme, JwtClaimTypes.Name, JwtClaimTypes.Role));
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
    }

    [Fact]
    public async Task SendRequestAsync_SecuredRoute_WithholdsCredentialHeadersFromPipelineData()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/no-echo", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/no-echo");
        context.User = CreateAuthenticatedUser();
        context.Request.Headers.Authorization = "Bearer some.access.token";
        context.Request.Headers.Cookie = "session=secret";
        context.Request.Headers["X-Correlation-Id"] = "abc";
        await _service.SendRequestAsync(context);

        var headers = receivedInput?["headers"];
        Assert.NotNull(headers);
        Assert.Null(headers!["Authorization"]);
        Assert.Null(headers["Cookie"]);
        Assert.Equal("abc", headers["X-Correlation-Id"]?.ToString());
    }

    /// <remarks>
    /// An anonymous route does not imply the pipeline may see the caller's credential. Callers may
    /// present a token to a route that does not require one - every app on the platform attaches it
    /// per host, not per route - and that token must not reach the pipeline data, which is echoed
    /// back in the response and can be persisted or forwarded by downstream nodes.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_AnonymousRoute_WithholdsCredentialHeadersFromPipelineData()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/public", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: true);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/public");
        context.Request.Headers.Authorization = "Bearer some.access.token";
        context.Request.Headers.Cookie = "session=secret";
        context.Request.Headers["X-Correlation-Id"] = "abc";
        await _service.SendRequestAsync(context);

        var headers = receivedInput?["headers"];
        Assert.NotNull(headers);
        Assert.Null(headers!["Authorization"]);
        Assert.Null(headers["Cookie"]);
        Assert.Equal("abc", headers["X-Correlation-Id"]?.ToString());
    }

    /// <remarks>
    /// FromTeamsBot validates the inbound Bot Framework token itself, because that token is not
    /// issued by the platform identity service and so cannot pass the adapter's own gate. It is the
    /// only trigger that opts in.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_RouteReceivingCredentialHeaders_ForwardsTheAuthorizationHeader()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/bot", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: true, receivesCredentialHeaders: true);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/bot");
        context.Request.Headers.Authorization = "Bearer bot.framework.token";
        await _service.SendRequestAsync(context);

        Assert.Equal("Bearer bot.framework.token",
            receivedInput?["headers"]?["Authorization"]?.ToString());
    }

    /// <remarks>
    /// The two flags are orthogonal: opting in to the credential headers is what forwards them,
    /// whether or not the adapter authorized the caller first.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_SecuredRouteReceivingCredentialHeaders_ForwardsTheAuthorizationHeader()
    {
        JsonNode? receivedInput = null;
        var options = CreateRouteOptions("/api/secured-bot", HttpMethod.Post, input =>
        {
            receivedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, receivesCredentialHeaders: true);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/secured-bot");
        context.User = CreateAuthenticatedUser();
        context.Request.Headers.Authorization = "Bearer some.access.token";
        await _service.SendRequestAsync(context);

        Assert.Equal("Bearer some.access.token",
            receivedInput?["headers"]?["Authorization"]?.ToString());
    }

    #endregion

    #region Audit

    [Fact]
    public async Task SendRequestAsync_MissingToken_AuditsTheDenial()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post, allowAnonymous: false);
        _service.CreateRoute(options);

        await _service.SendRequestAsync(CreateHttpContext("POST", $"/{TenantId}/api/audit"));

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Contains("Denied POST /api/audit", entry.Message);
        Assert.Contains("no valid access token", entry.Message);
    }

    [Fact]
    public async Task SendRequestAsync_ForeignTenant_AuditsTheDenial()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/audit");
        context.User = CreateAuthenticatedUserOfTenant("otherTenant");
        await _service.SendRequestAsync(context);

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Contains("otherTenant", entry.Message);
        Assert.Contains("does not serve this tenant", entry.Message);
    }

    [Fact]
    public async Task SendRequestAsync_MissingRole_AuditsTheDeniedRoles()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post,
            allowAnonymous: false, requiredRoles: ["TenantAdmin", "CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/audit");
        context.User = CreateAuthenticatedUser("Reader");
        await _service.SendRequestAsync(context);

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Contains("TenantAdmin, CommunicationAdmin", entry.Message);
    }

    [Fact]
    public async Task SendRequestAsync_AuthorizedCaller_AuditsSubjectTenantAndRoles()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post,
            allowAnonymous: false, requiredRoles: ["TenantAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/audit");
        context.User = CreateAuthenticatedUser("TenantAdmin", "Reader");
        await _service.SendRequestAsync(context);

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Information, entry.Level);
        Assert.Contains("Allowed POST /api/audit", entry.Message);
        Assert.Contains("660000000000000000000042", entry.Message);
        Assert.Contains(TenantId, entry.Message);
        Assert.Contains("TenantAdmin, Reader", entry.Message);
    }

    /// <remarks>
    /// The audit trail must never carry the credential it was shown, so the whole recorded
    /// message set is checked rather than one branch.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_AuditedDecisions_NeverContainTheToken()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post,
            allowAnonymous: false, requiredRoles: ["TenantAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/audit");
        context.User = CreateAuthenticatedUser("TenantAdmin");
        context.Request.Headers.Authorization = "Bearer some.access.token";
        await _service.SendRequestAsync(context);

        Assert.NotEmpty(_eventService.Events);
        Assert.All(_eventService.Events, entry =>
        {
            Assert.DoesNotContain("Bearer", entry.Message);
            Assert.DoesNotContain("some.access.token", entry.Message);
        });
    }

    [Fact]
    public async Task SendRequestAsync_AnonymousRoute_AuditsAtDebugLevelWhenEnabled()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post, allowAnonymous: true);
        _service.CreateRoute(options);

        await _service.SendRequestAsync(CreateHttpContext("POST", $"/{TenantId}/api/audit"));

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Debug, entry.Level);
        Assert.Contains("Allowed anonymous POST /api/audit", entry.Message);
    }

    /// <remarks>
    /// An anonymous route serves public webhooks, whose volume would dominate an event log that
    /// nothing prunes, so storing an event per invocation is opt-in. The decision still reaches
    /// the adapter log at debug level, so disabling this hides nothing.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_AnonymousRoute_StoresNoEventWhenDisabled()
    {
        var service = CreateService(auditAnonymousInvocations: false);
        service.CreateRoute(CreateRouteOptions("/api/audit", HttpMethod.Post, allowAnonymous: true));

        var result = await service.SendRequestAsync(CreateHttpContext("POST", $"/{TenantId}/api/audit"));

        Assert.True(result);
        Assert.Empty(_eventService.Events);
    }

    #endregion

    #region Helpers

    private static HttpRequestOptions CreateRouteOptions(string route, HttpMethod method,
        Func<JsonNode, Task<JsonNode?>>? executeFunc = null, bool allowAnonymous = true,
        string[]? requiredRoles = null, bool receivesCredentialHeaders = false)
    {
        executeFunc ??= _ => Task.FromResult<JsonNode?>(null);
        return new HttpRequestOptions(route, method, executeFunc, allowAnonymous, requiredRoles ?? [],
            receivesCredentialHeaders);
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(params string[] roles)
    {
        return CreateAuthenticatedUserOfTenant(TenantId, roles);
    }

    private static ClaimsPrincipal CreateAuthenticatedUserOfTenant(string? tenantId, params string[] roles)
    {
        List<Claim> claims = [new(JwtClaimTypes.Subject, "660000000000000000000042")];
        if (tenantId != null)
        {
            claims.Add(new Claim(TenantIdClaim, tenantId));
        }

        claims.AddRange(roles.Select(r => new Claim(JwtClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme,
            JwtClaimTypes.Name, JwtClaimTypes.Role));
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

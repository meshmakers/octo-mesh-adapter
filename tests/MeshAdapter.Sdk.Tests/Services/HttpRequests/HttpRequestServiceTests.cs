using Meshmakers.Octo.Sdk.Common.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Duende.IdentityModel;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

    private HttpRequestService CreateService(bool auditAnonymousInvocations,
        ServiceTokenEnforcementMode serviceTokenEnforcement = ServiceTokenEnforcementMode.Enforce)
    {
        return new HttpRequestService(
            Options.Create(new AdapterOptions { DedicatedTenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration
            {
                AuditAnonymousInvocations = auditAnonymousInvocations,
                ServiceTokenEnforcement = serviceTokenEnforcement
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
        var responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var parsed = JsonNode.Parse(responseBody)!.AsObject();
        Assert.Equal("success", parsed["result"]?.ToString());
    }

    // AB#5370: a node that throws used to answer with a 500 and an EMPTY body — nothing between
    // the route delegate and Kestrel turned the exception into a response. The node's message is
    // the one thing the caller can act on, so it is the error body now.

    /// <summary>
    ///     The orchestrator's wrapper; its constructors are not public, so the test derives from it
    ///     to build the chain a failed execution really produces.
    /// </summary>
    private sealed class OrchestratorException : DataPipelineException
    {
        public OrchestratorException(string message) : base(message)
        {
        }

        public OrchestratorException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    private static async Task<(int Status, string? ContentType, JsonObject Body)> ReadErrorResponse(
        HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, context.Response.ContentType, JsonNode.Parse(body)!.AsObject());
    }

    [Fact]
    public async Task SendRequestAsync_NodeThrows_Answers500WithTheNodesMessageAsErrorBody()
    {
        // The chain the orchestrator builds: DataPipelineException("Error in node '…': …") around
        // the node's own PipelineExecutionException.
        var nodeError = MeshAdapterPipelineExecutionException.MailFolderListingFailed("Graph",
            "Microsoft Graph denied access to mailbox 'box@example.com' (403).", new Exception("http"));
        var options = CreateRouteOptions("/api/throws", HttpMethod.Post, _ =>
            throw new OrchestratorException($"Error in node 'ListMailFolders[0]': {nodeError.Message}", nodeError));
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/throws");
        context.Response.Body = new MemoryStream();

        Assert.True(await _service.SendRequestAsync(context));

        var (status, contentType, body) = await ReadErrorResponse(context);
        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("application/json", contentType);
        Assert.Equal("Microsoft 365: Microsoft Graph denied access to mailbox 'box@example.com' (403).",
            body["errorMessage"]!.GetValue<string>());
        Assert.Single(body);
    }

    [Fact]
    public async Task SendRequestAsync_OtherExceptionThrows_Answers500WithItsMessage()
    {
        var options = CreateRouteOptions("/api/boom", HttpMethod.Get, _ =>
            throw new InvalidOperationException("Mail folder path '' is empty"));
        _service.CreateRoute(options);

        var context = CreateHttpContext("GET", $"/{TenantId}/api/boom");
        context.Response.Body = new MemoryStream();

        Assert.True(await _service.SendRequestAsync(context));

        var (status, _, body) = await ReadErrorResponse(context);
        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("Mail folder path '' is empty", body["errorMessage"]!.GetValue<string>());
    }

    [Fact]
    public void ExtractNodeErrorMessage_UnwrapsToTheInnermostPipelineException()
    {
        var inner = MeshAdapterPipelineExecutionException.MailFolderChannelInvalid("Exchange");
        var wrapped = new OrchestratorException(
            $"Error in node 'ForEach[1]': Error in node 'ListMailFolders[0]': {inner.Message}",
            new OrchestratorException($"Error in node 'ListMailFolders[0]': {inner.Message}", inner));

        Assert.Equal(inner.Message, HttpRequestService.ExtractNodeErrorMessage(wrapped));
    }

    [Fact]
    public void ExtractNodeErrorMessage_StripsThePrefixChainWhenNoPipelineExceptionIsInside()
    {
        var wrapped = new OrchestratorException(
            "Error in node 'ForEach[1]': Error in node 'MakeHttpRequest[2]': Object reference not set",
            new NullReferenceException("Object reference not set"));

        Assert.Equal("Object reference not set", HttpRequestService.ExtractNodeErrorMessage(wrapped));
        // A prefix that is not the orchestrator's stays as it is.
        Assert.Equal("Error in node without the colon", HttpRequestService.ExtractNodeErrorMessage(
            new OrchestratorException("Error in node without the colon")));
        Assert.Equal("Exception", HttpRequestService.ExtractNodeErrorMessage(new Exception("")));
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
    /// Since AB#5032 identity stamps tenant_id on client-credentials tokens, so a machine caller of
    /// the adapter's own tenant holding the required role is served (AB#5628).
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_ServiceTokenOfOwnTenantWithRole_ExecutesFunc()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/machine", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = CreateServiceToken(TenantId, "CommunicationAdmin");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Information, entry.Level);
        Assert.Contains("for subject machine-client", entry.Message);
    }

    /// <remarks>
    /// AB#5628: a client of another tenant holding a role whose name the route requires must not
    /// get through - the tenant check runs before the role check.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_ServiceTokenOfAnotherTenant_ReturnsForbiddenAndRecordsWarning()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/machine", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = CreateServiceToken("otherTenant", "CommunicationAdmin");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("error_code=\"tenant_mismatch\"", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.False(executed);
        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Contains("Denied", entry.Message);
        Assert.Contains("client machine-client", entry.Message);
        Assert.Contains("'otherTenant'", entry.Message);
    }

    [Fact]
    public async Task SendRequestAsync_ServiceTokenWithoutTenant_ReturnsForbidden()
    {
        var executed = false;
        var options = CreateRouteOptions("/api/machine", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["CommunicationAdmin"]);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = CreateServiceToken(null, "CommunicationAdmin");
        var result = await _service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(executed);
        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Contains("carries no tenant claim", entry.Message);
    }

    [Fact]
    public async Task SendRequestAsync_ServiceTokenOfAnotherTenantInWarnMode_ExecutesAndRecordsWarning()
    {
        var service = CreateService(auditAnonymousInvocations: false, ServiceTokenEnforcementMode.Warn);
        var executed = false;
        var options = CreateRouteOptions("/api/machine", HttpMethod.Post, _ =>
        {
            executed = true;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, requiredRoles: ["CommunicationAdmin"]);
        service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = CreateServiceToken("otherTenant", "CommunicationAdmin");
        var result = await service.SendRequestAsync(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(executed);
        Assert.Contains(_eventService.Events, e => e.Level == RtEventLevelsEnum.Warning &&
                                                   e.Message.Contains("would be denied") &&
                                                   e.Message.Contains("client machine-client"));
    }

    [Fact]
    public async Task SendRequestAsync_ServiceTokenOfAnotherTenantInWarnMode_StillNeedsTheRole()
    {
        var service = CreateService(auditAnonymousInvocations: false, ServiceTokenEnforcementMode.Warn);
        service.CreateRoute(CreateRouteOptions("/api/machine", HttpMethod.Post, allowAnonymous: false,
            requiredRoles: ["CommunicationAdmin"]));

        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine");
        context.User = CreateServiceToken("otherTenant", "Reader");
        await service.SendRequestAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <remarks>
    /// A user token whose subject arrives as a name-identifier claim (a different inbound claim
    /// mapping) must still be treated as a user token and compared, not as a service token.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_UserTokenWithNameIdentifierOfAnotherTenant_ReturnsForbidden()
    {
        var service = CreateService(auditAnonymousInvocations: false, ServiceTokenEnforcementMode.Warn);
        service.CreateRoute(CreateRouteOptions("/api/tenant", HttpMethod.Post, allowAnonymous: false));

        var context = CreateHttpContext("POST", $"/{TenantId}/api/tenant");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "660000000000000000000042"), new Claim(TenantIdClaim, "otherTenant")],
            JwtBearerDefaults.AuthenticationScheme, JwtClaimTypes.Name, JwtClaimTypes.Role));
        await service.SendRequestAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
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

    /// <remarks>
    /// The audit entry is read by an operator, so a missing claim must not be reported as the
    /// foreign tenant '' - it names a different failure than a token minted for another tenant.
    /// </remarks>
    [Fact]
    public async Task SendRequestAsync_UserTokenWithoutTenant_AuditsTheMissingClaim()
    {
        var options = CreateRouteOptions("/api/audit", HttpMethod.Post, allowAnonymous: false);
        _service.CreateRoute(options);

        var context = CreateHttpContext("POST", $"/{TenantId}/api/audit");
        context.User = CreateAuthenticatedUserOfTenant(null);
        await _service.SendRequestAsync(context);

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Warning, entry.Level);
        Assert.Contains("no tenant claim", entry.Message);
        Assert.DoesNotContain("does not serve this tenant", entry.Message);
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
        return new HttpRequestOptions(route, method, (input, _) => executeFunc(input), allowAnonymous,
            requiredRoles ?? [],
            receivesCredentialHeaders);
    }

    [Fact]
    public async Task SendRequestAsync_AuthenticatedCaller_PassesVerifiedPrincipalAndDataRootSubset()
    {
        // AB#4975: the verified caller reaches the pipeline twice — as VerifiedPrincipal on the
        // ExecuteFunc (server-side stamp) and as the safe $.principal subset in the data root.
        VerifiedPrincipal? capturedPrincipal = null;
        JsonNode? capturedInput = null;
        _service.CreateRoute(new HttpRequestOptions("/upload", HttpMethod.Post, (input, caller) =>
        {
            capturedPrincipal = caller.Principal;
            capturedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, []));

        var context = CreateHttpContext("POST", $"/{TenantId}/upload");
        context.User = CreateAuthenticatedUser("AccountingEmployee", "AccountingManagement");

        Assert.True(await _service.SendRequestAsync(context));

        Assert.NotNull(capturedPrincipal);
        Assert.Equal("660000000000000000000042", capturedPrincipal!.SubjectId);
        Assert.Equal(TenantId, capturedPrincipal.TenantId);
        Assert.Equal(["AccountingEmployee", "AccountingManagement"], capturedPrincipal.Roles);

        var principalNode = capturedInput?["principal"];
        Assert.NotNull(principalNode);
        Assert.Equal("660000000000000000000042", principalNode!["subjectId"]!.GetValue<string>());
        Assert.Equal(2, principalNode["roles"]!.AsArray().Count);
    }

    [Fact]
    public async Task SendRequestAsync_AnonymousRoute_NoPrincipal()
    {
        VerifiedPrincipal? capturedPrincipal = new("sentinel", null, null, null, []);
        var capturedToken = "sentinel";
        JsonNode? capturedInput = null;
        _service.CreateRoute(new HttpRequestOptions("/open", HttpMethod.Post, (input, caller) =>
        {
            capturedPrincipal = caller.Principal;
            capturedToken = caller.RawAccessToken;
            capturedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: true, []));

        var context = CreateHttpContext("POST", $"/{TenantId}/open");

        Assert.True(await _service.SendRequestAsync(context));
        Assert.Null(capturedPrincipal);
        Assert.Null(capturedToken);
        Assert.Null(capturedInput?["principal"]);
    }

    #region Caller access token (AB#5031)

    [Fact]
    public async Task SendRequestAsync_AuthenticatedCaller_PassesRawTokenToExecuteFuncButNeverIntoTheDataRoot()
    {
        // AB#5031: a node acting on behalf of the caller (delegation grant) needs the raw bearer
        // token as subject_token. It reaches the trigger's execute delegate ONLY — the data root is
        // echoed back in the HTTP response, persisted by SetPipelineExecutionResult@1 and shown in
        // the Studio debug panel, so the credential must not appear anywhere in it.
        const string rawToken = "ey.super.secret-caller-token";

        string? capturedToken = null;
        JsonNode? capturedInput = null;
        _service.CreateRoute(new HttpRequestOptions("/aiprompt", HttpMethod.Post, (input, caller) =>
        {
            capturedToken = caller.RawAccessToken;
            capturedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, []));

        var context = CreateHttpContext("POST", $"/{TenantId}/aiprompt");
        context.User = CreateAuthenticatedUser("AccountingEmployee");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";

        Assert.True(await _service.SendRequestAsync(context));

        Assert.Equal(rawToken, capturedToken);

        // Negative assertion 1: the Authorization header is still filtered out of $.headers.
        Assert.NotNull(capturedInput);
        var headers = capturedInput!["headers"];
        Assert.NotNull(headers);
        Assert.Null(headers!["Authorization"]);

        // Negative assertion 2: the token appears NOWHERE in the whole data root — not under a
        // differently named key, not inside $.principal, not in a body echo.
        Assert.DoesNotContain(rawToken, capturedInput.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendRequestAsync_CredentialHeaderRoute_StillKeepsTheTokenOutOfTheDataRootDecision()
    {
        // The credential-header opt-in (FromTeamsBot) is governed by ReceivesCredentialHeaders and is
        // deliberately untouched by AB#5031: with it set, the header travels as before — the raw
        // token on the caller context is an independent, additional channel.
        const string rawToken = "ey.bot.token";

        string? capturedToken = null;
        JsonNode? capturedInput = null;
        _service.CreateRoute(new HttpRequestOptions("/bot", HttpMethod.Post, (input, caller) =>
        {
            capturedToken = caller.RawAccessToken;
            capturedInput = input;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, [], receivesCredentialHeaders: true));

        var context = CreateHttpContext("POST", $"/{TenantId}/bot");
        context.User = CreateAuthenticatedUser("AccountingEmployee");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";

        Assert.True(await _service.SendRequestAsync(context));

        Assert.Equal(rawToken, capturedToken);
        Assert.Equal($"Bearer {rawToken}", capturedInput!["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendRequestAsync_NonBearerScheme_YieldsNoCallerToken()
    {
        // A credential the platform gate did not mint must never be handed on as if it were a bearer
        // token — a delegation request built from it would fail at the identity service with an
        // unhelpful error instead of never being attempted.
        string? capturedToken = "sentinel";
        _service.CreateRoute(new HttpRequestOptions("/basic", HttpMethod.Post, (_, caller) =>
        {
            capturedToken = caller.RawAccessToken;
            return Task.FromResult<JsonNode?>(null);
        }, allowAnonymous: false, []));

        var context = CreateHttpContext("POST", $"/{TenantId}/basic");
        context.User = CreateAuthenticatedUser("AccountingEmployee");
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        Assert.True(await _service.SendRequestAsync(context));

        Assert.Null(capturedToken);
    }


    #endregion

    #region Response guard (AB#5674)

    private const string SecretHeader = "x-secret-header-value-9d1";
    private const string BearerValue = "ey.raw.bearer.token.5674";
    private const string CookieValue = "session=cookie-value-5674";
    private const string BodyMarker = "body-marker-5674";
    private const string QueryMarker = "query-marker-5674";
    private const string SubjectId = "660000000000000000000042";
    private static readonly string[] ReservedKeys =
        ["path", "method", "principal", "headers", "body", "bodyEncoding", "query", "files", "formData",
            "contentType", "contentLength"];

    private sealed class ListLogger : ILogger<HttpRequestService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    /// <summary>The unshaped route: the pipeline answers with its whole data root plus one result field.</summary>
    private static Task<JsonNode?> EchoDataRoot(JsonNode input)
    {
        var root = input.DeepClone().AsObject();
        root["result"] = "done";
        return Task.FromResult<JsonNode?>(root);
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static void AddRequestData(HttpContext context)
    {
        context.Request.Headers["X-Custom"] = SecretHeader;
        context.Request.Headers.Authorization = $"Bearer {BearerValue}";
        context.Request.Headers.Cookie = CookieValue;
        context.Request.QueryString = new QueryString($"?q={QueryMarker}");
    }

    private static void AssertEnvelopeAbsent(string responseText, params string[] forbiddenValues)
    {
        var parsed = JsonNode.Parse(responseText)!.AsObject();
        foreach (var key in ReservedKeys)
        {
            Assert.False(parsed.ContainsKey(key), $"reserved key '{key}' is in the response");
        }

        Assert.Equal("done", parsed["result"]?.ToString());
        foreach (var value in new[] { SecretHeader, BearerValue, CookieValue, BodyMarker, QueryMarker, SubjectId }
                     .Concat(forbiddenValues))
        {
            Assert.DoesNotContain(value, responseText, StringComparison.Ordinal);
        }
    }

    private async Task<string> CallAsync(HttpRequestService service, HttpContext context)
    {
        context.Response.Body = new MemoryStream();
        Assert.True(await service.SendRequestAsync(context));
        return await ReadBodyAsync(context);
    }

    [Fact]
    public async Task Guard_UnshapedRoute_AuthenticatedCaller_ResponseCarriesNoRequestEnvelope()
    {
        _service.CreateRoute(CreateRouteOptions("/api/echo", HttpMethod.Post, EchoDataRoot, allowAnonymous: false));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/echo", $"{{\"b\":\"{BodyMarker}\"}}",
            "application/json");
        AddRequestData(context);
        context.User = CreateAuthenticatedUser("AccountingEmployee");

        AssertEnvelopeAbsent(await CallAsync(_service, context), "AccountingEmployee");
    }

    [Fact]
    public async Task Guard_FromHttpRequest1_AnonymousCaller_ResponseCarriesNoRequestEnvelope()
    {
        _service.CreateRoute(CreateRouteOptions("/api/anon", HttpMethod.Post, EchoDataRoot));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/anon", BodyMarker, "text/plain");
        AddRequestData(context);

        AssertEnvelopeAbsent(await CallAsync(_service, context));
    }

    [Fact]
    public async Task Guard_RouteWithRequiredRoles_ResponseCarriesNoRolesOrEnvelope()
    {
        _service.CreateRoute(CreateRouteOptions("/api/roles-echo", HttpMethod.Post, EchoDataRoot,
            allowAnonymous: false, requiredRoles: ["BillingAdmin"]));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/roles-echo", $"{{\"b\":\"{BodyMarker}\"}}",
            "application/json");
        AddRequestData(context);
        context.User = CreateAuthenticatedUser("BillingAdmin", "OtherRole");

        AssertEnvelopeAbsent(await CallAsync(_service, context), "BillingAdmin", "OtherRole");
    }

    [Fact]
    public async Task Guard_ServiceToken_ResponseCarriesNoClientIdOrEnvelope()
    {
        _service.CreateRoute(CreateRouteOptions("/api/machine-echo", HttpMethod.Post, EchoDataRoot,
            allowAnonymous: false));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/machine-echo", $"{{\"b\":\"{BodyMarker}\"}}",
            "application/json");
        AddRequestData(context);
        context.User = CreateServiceToken(TenantId, "MachineRole");

        AssertEnvelopeAbsent(await CallAsync(_service, context), "machine-client", "MachineRole");
    }

    [Fact]
    public async Task Guard_CredentialHeaderRoute_TeamsBotAuthorizationIsNeverInTheResponse()
    {
        // FromTeamsBot@1 opts into credential headers, so its Authorization value IS in $.headers.
        const string botCredential = "Bearer bot-framework-jwt-5674";
        _service.CreateRoute(CreateRouteOptions("/api/bot-echo", HttpMethod.Post, input =>
        {
            Assert.Equal(botCredential, input["headers"]!["Authorization"]!.GetValue<string>());
            return EchoDataRoot(input);
        }, receivesCredentialHeaders: true));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/bot-echo", $"{{\"b\":\"{BodyMarker}\"}}",
            "application/json");
        AddRequestData(context);
        context.Request.Headers.Authorization = botCredential;

        AssertEnvelopeAbsent(await CallAsync(_service, context), botCredential, "bot-framework-jwt-5674");
    }

    [Fact]
    public async Task Guard_MultipartUpload_FilesAndFormDataAreNotEchoed()
    {
        const string fileContent = "file-content-5674";
        _service.CreateRoute(CreateRouteOptions("/api/upload-echo", HttpMethod.Post, input =>
        {
            Assert.NotNull(input["files"]);
            Assert.NotNull(input["formData"]);
            return EchoDataRoot(input);
        }));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/upload-echo", "x", "multipart/form-data; boundary=b");
        var bytes = Encoding.UTF8.GetBytes(fileContent);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "upload5674.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
        context.Request.Form = new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["note"] = BodyMarker },
            new FormFileCollection { file });
        AddRequestData(context);

        AssertEnvelopeAbsent(await CallAsync(_service, context), fileContent, "upload5674.txt",
            Convert.ToBase64String(bytes), "multipart/form-data");
    }

    [Fact]
    public async Task Guard_BinaryBody_BodyAndBase64AreNotEchoed()
    {
        var bytes = Encoding.UTF8.GetBytes(BodyMarker);
        _service.CreateRoute(CreateRouteOptions("/api/binary-echo", HttpMethod.Post, input =>
        {
            Assert.Equal("base64", input["bodyEncoding"]!.GetValue<string>());
            return EchoDataRoot(input);
        }));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/binary-echo");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/octet-stream";
        AddRequestData(context);

        AssertEnvelopeAbsent(await CallAsync(_service, context), Convert.ToBase64String(bytes),
            "application/octet-stream");
    }

    [Fact]
    public async Task Guard_OperationFailedErrorShape_PassesThroughUnchanged()
    {
        // What FromHttpRequest@1/@2 answer when a repository operation fails: no request keys, so
        // the guard has nothing to remove and the body stays byte-identical.
        var error = new JsonObject
        {
            ["message"] = "Operation failed",
            ["details"] = new JsonArray(new JsonObject { ["code"] = null, ["description"] = "x" })
        };
        _service.CreateRoute(CreateRouteOptions("/api/failed", HttpMethod.Post,
            _ => Task.FromResult<JsonNode?>(error.DeepClone())));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/failed");

        Assert.Equal(error.ToJsonString(), await CallAsync(_service, context));
    }

    [Fact]
    public async Task Guard_NodeThrows_ErrorBodyStaysTheMessageOnly()
    {
        _service.CreateRoute(CreateRouteOptions("/api/throws-echo", HttpMethod.Post,
            _ => throw new InvalidOperationException("node failed")));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/throws-echo", BodyMarker, "text/plain");
        AddRequestData(context);

        var text = await CallAsync(_service, context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("{\"errorMessage\":\"node failed\"}", text);
    }

    [Fact]
    public async Task Guard_WholeCopiesOfHeadersAndPrincipal_AreRemovedEverywhere()
    {
        _service.CreateRoute(CreateRouteOptions("/api/copies", HttpMethod.Post, input =>
        {
            var root = new JsonObject
            {
                ["status"] = "ok",
                ["debug"] = new JsonObject
                {
                    ["h"] = input["headers"]!.DeepClone(),
                    ["keep"] = "kept"
                },
                ["items"] = new JsonArray(
                    new JsonObject { ["id"] = 1, ["principal"] = input["principal"]!.DeepClone() },
                    new JsonObject { ["id"] = 2, ["headers"] = input["headers"]!.DeepClone() },
                    input["principal"]!.DeepClone()),
                ["createdBy"] = input["principal"]!["email"]!.DeepClone()
            };
            return Task.FromResult<JsonNode?>(root);
        }, allowAnonymous: false));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/copies");
        AddRequestData(context);
        context.User = CreateAuthenticatedUser("AccountingEmployee");
        context.User.Identities.First().AddClaim(new Claim(JwtClaimTypes.Email, "caller5674@example.org"));

        var text = await CallAsync(_service, context);

        var parsed = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("ok", parsed["status"]!.ToString());
        Assert.Equal("kept", parsed["debug"]!["keep"]!.ToString());
        Assert.Null(parsed["debug"]!["h"]);
        var items = parsed["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Null(items[0]!["principal"]);
        Assert.Null(items[1]!["headers"]);
        Assert.Equal(1, items[0]!["id"]!.GetValue<int>());
        // A single field the author maps on purpose stays: it is their explicit output.
        Assert.Equal("caller5674@example.org", parsed["createdBy"]!.ToString());
        Assert.DoesNotContain(SecretHeader, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountingEmployee", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_ShapedResponse_IsByteIdentical()
    {
        const string shaped =
            "{\"status\":\"ok\",\"message\":\"created\",\"result\":{\"items\":[{\"id\":1,\"name\":\"a\"},{\"id\":2,\"name\":\"b\"}]}}";
        _service.CreateRoute(CreateRouteOptions("/api/shaped", HttpMethod.Post,
            _ => Task.FromResult<JsonNode?>(JsonNode.Parse(shaped))));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/shaped", $"{{\"b\":\"{BodyMarker}\"}}",
            "application/json");
        AddRequestData(context);

        Assert.Equal(shaped, await CallAsync(_service, context));
    }

    [Theory]
    [InlineData("\"plain text\"")]
    [InlineData("42")]
    [InlineData("[1,2,3]")]
    public async Task Guard_NonObjectResponse_IsUnchanged(string json)
    {
        _service.CreateRoute(CreateRouteOptions("/api/scalar", HttpMethod.Get,
            _ => Task.FromResult<JsonNode?>(JsonNode.Parse(json))));
        var context = CreateHttpContext("GET", $"/{TenantId}/api/scalar");

        Assert.Equal(JsonNode.Parse(json)!.ToJsonString(), await CallAsync(_service, context));
    }

    [Fact]
    public async Task Guard_NullResponse_StillGivesTheEmptyBody()
    {
        _service.CreateRoute(CreateRouteOptions("/api/nothing", HttpMethod.Get, _ => Task.FromResult<JsonNode?>(null)));
        var context = CreateHttpContext("GET", $"/{TenantId}/api/nothing");

        Assert.Equal(string.Empty, await CallAsync(_service, context));
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task Guard_RemovalIsLoggedOncePerRouteAndKeySet_WithKeyNamesOnly()
    {
        var logger = new ListLogger();
        var service = new HttpRequestService(
            Options.Create(new AdapterOptions { DedicatedTenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration()), _eventService, logger);
        service.CreateRoute(CreateRouteOptions("/api/logged", HttpMethod.Post, EchoDataRoot, allowAnonymous: false));

        for (var i = 0; i < 3; i++)
        {
            var context = CreateHttpContext("POST", $"/{TenantId}/api/logged", $"{{\"b\":\"{BodyMarker}\"}}",
                "application/json");
            AddRequestData(context);
            context.User = CreateAuthenticatedUser("AccountingEmployee");
            await CallAsync(service, context);
        }

        var removals = logger.Messages.Where(m => m.Contains("removed from the response")).ToList();
        var single = Assert.Single(removals);
        Assert.Contains("principal", single);
        Assert.Contains("headers", single);
        foreach (var value in new[] { SecretHeader, BearerValue, CookieValue, BodyMarker, QueryMarker, SubjectId,
                     "AccountingEmployee" })
        {
            Assert.DoesNotContain(value, single, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Guard_ForEachResponseWithVaryingLength_IsLoggedOnce()
    {
        var logger = new ListLogger();
        var service = new HttpRequestService(
            Options.Create(new AdapterOptions { DedicatedTenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration()), _eventService, logger);
        var count = 1;
        service.CreateRoute(CreateRouteOptions("/api/each", HttpMethod.Post, input =>
        {
            var items = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                items.Add(new JsonObject { ["id"] = i, ["principal"] = input["principal"]!.DeepClone() });
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["items"] = items });
        }, allowAnonymous: false));

        for (count = 1; count <= 4; count++)
        {
            var context = CreateHttpContext("POST", $"/{TenantId}/api/each");
            context.User = CreateAuthenticatedUser("AccountingEmployee");
            await CallAsync(service, context);
        }

        Assert.Single(logger.Messages, m => m.Contains("removed from the response"));
    }

    [Fact]
    public async Task Guard_ArrayRoot_WholeCopiesOfHeadersAndPrincipalAreRemoved()
    {
        _service.CreateRoute(CreateRouteOptions("/api/array-root", HttpMethod.Post, input =>
            Task.FromResult<JsonNode?>(new JsonArray(
                new JsonObject { ["id"] = 1 },
                input["headers"]!.DeepClone(),
                input["principal"]!.DeepClone())), allowAnonymous: false));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/array-root");
        AddRequestData(context);
        context.User = CreateAuthenticatedUser("AccountingEmployee");

        var text = await CallAsync(_service, context);

        Assert.Equal("[{\"id\":1}]", text);
    }

    [Fact]
    public async Task Guard_EmptyHeaders_DoNotMakeUnrelatedEmptyObjectsACopy()
    {
        // Every credential header filtered away leaves $.headers empty; "metadata": {} is then no copy.
        _service.CreateRoute(CreateRouteOptions("/api/empty", HttpMethod.Post,
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["status"] = "ok", ["metadata"] = new JsonObject() })));
        var context = CreateHttpContext("POST", $"/{TenantId}/api/empty");
        context.Request.Headers.Authorization = $"Bearer {BearerValue}";

        Assert.Equal("{\"status\":\"ok\",\"metadata\":{}}", await CallAsync(_service, context));
    }

    [Fact]
    public async Task Guard_ShapedResponse_LogsNothing()
    {
        var logger = new ListLogger();
        var service = new HttpRequestService(
            Options.Create(new AdapterOptions { DedicatedTenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration()), _eventService, logger);
        service.CreateRoute(CreateRouteOptions("/api/quiet", HttpMethod.Get,
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["status"] = "ok" })));

        await CallAsync(service, CreateHttpContext("GET", $"/{TenantId}/api/quiet"));

        Assert.DoesNotContain(logger.Messages, m => m.Contains("removed from the response"));
    }

    #endregion

    private static ClaimsPrincipal CreateAuthenticatedUser(params string[] roles)
    {
        return CreateAuthenticatedUserOfTenant(TenantId, roles);
    }

    /// <summary>A client-credentials principal: no subject, only client id, roles and the tenant identity stamped.</summary>
    private static ClaimsPrincipal CreateServiceToken(string? tenantId, params string[] roles)
    {
        List<Claim> claims = [new("client_id", "machine-client")];
        if (tenantId != null)
        {
            claims.Add(new Claim(TenantIdClaim, tenantId));
        }

        claims.AddRange(roles.Select(r => new Claim(JwtClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme,
            JwtClaimTypes.Name, JwtClaimTypes.Role));
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

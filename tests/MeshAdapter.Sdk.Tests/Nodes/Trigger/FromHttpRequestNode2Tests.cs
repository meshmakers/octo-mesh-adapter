using System.Security.Claims;
using System.Text.Json.Nodes;
using FakeItEasy;
using IdentityModel;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using MeshAdapter.Sdk.Tests.Services.HttpRequests;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

public class FromHttpRequestNode2Tests
{
    private const string TenantId = "testTenant";

    private readonly RecordingAdapterEventService _eventService = new();
    private readonly HttpRequestService _httpRequestService;

    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly ITriggerContext _triggerContext = A.Fake<ITriggerContext>();

    public FromHttpRequestNode2Tests()
    {
        _httpRequestService = new HttpRequestService(
            Options.Create(new AdapterOptions { TenantId = TenantId }),
            Options.Create(new MeshAdapterConfiguration { AuditAnonymousInvocations = false }),
            _eventService, NullLogger<HttpRequestService>.Instance);

        A.CallTo(() => _triggerContext.TenantId).Returns(TenantId);
        A.CallTo(() => _triggerContext.NodeContext).Returns(_nodeContext);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Returns(Task.FromResult<object?>(null));
    }

    private FromHttpRequestNode2 CreateNode(bool allowAnonymous, params string[] requiredRoles)
    {
        A.CallTo(() => _nodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration2>())
            .Returns(new FromHttpRequestNodeConfiguration2
            {
                Path = "/webhook", Method = HttpMethod.Post, AllowAnonymous = allowAnonymous,
                RequiredRoles = requiredRoles
            });

        return new FromHttpRequestNode2(NullLogger<FromHttpRequestNode2>.Instance, _httpRequestService,
            _eventService);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = $"/{TenantId}/webhook";
        return context;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(params string[] roles)
    {
        List<Claim> claims =
        [
            new(JwtClaimTypes.Subject, "660000000000000000000042"),
            new("tenant_id", TenantId)
        ];
        claims.AddRange(roles.Select(r => new Claim(JwtClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme,
            JwtClaimTypes.Name, JwtClaimTypes.Role));
    }

    [Fact]
    public async Task StartAsync_AllowAnonymous_ExecutesThePipelineForTheConfiguredRoute()
    {
        var node = CreateNode(allowAnonymous: true);

        await node.StartAsync(_triggerContext);
        var handled = await _httpRequestService.SendRequestAsync(CreateHttpContext());

        Assert.True(handled);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<JsonNode>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_AnonymousNotAllowed_RejectsUnauthenticatedRequest()
    {
        var node = CreateNode(allowAnonymous: false);
        var context = CreateHttpContext();

        await node.StartAsync(_triggerContext);
        var handled = await _httpRequestService.SendRequestAsync(context);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<JsonNode>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StartAsync_NoRolesConfigured_AcceptsAnAuthenticatedCallerWithoutRoles()
    {
        var node = CreateNode(allowAnonymous: false);
        var context = CreateHttpContext();
        context.User = CreateAuthenticatedUser();

        await node.StartAsync(_triggerContext);
        var handled = await _httpRequestService.SendRequestAsync(context);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<JsonNode>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <remarks>
    /// Pins that the configured roles reach the route. Without this the pass-through in
    /// <c>FromHttpRequestNode2</c> could be replaced with an empty list and every other test
    /// would still pass, silently disabling role enforcement.
    /// </remarks>
    [Fact]
    public async Task StartAsync_ConfiguredRolesReachTheRoute()
    {
        var node = CreateNode(allowAnonymous: false, "TenantAdmin");
        var context = CreateHttpContext();
        context.User = CreateAuthenticatedUser("Reader");

        await node.StartAsync(_triggerContext);
        var handled = await _httpRequestService.SendRequestAsync(context);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<JsonNode>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StartAsync_ConfiguredRoleHeldByTheCaller_ExecutesThePipeline()
    {
        var node = CreateNode(allowAnonymous: false, "TenantAdmin");
        var context = CreateHttpContext();
        context.User = CreateAuthenticatedUser("TenantAdmin");

        await node.StartAsync(_triggerContext);
        var handled = await _httpRequestService.SendRequestAsync(context);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<JsonNode>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <remarks>
    /// The whole premise of Version 2 is that a route is secured unless the author opts out,
    /// so the defaults of the configuration record are asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void Configuration_DefaultsToSecuredWithoutRoleCheck()
    {
        var configuration = new FromHttpRequestNodeConfiguration2();

        Assert.False(configuration.AllowAnonymous);
        Assert.Empty(configuration.RequiredRoles);
    }

    /// <remarks>
    /// An unauthenticated route is a standing exposure, so its existence is audited once when the
    /// pipeline starts - independently of whether individual invocations are audited.
    /// </remarks>
    [Fact]
    public async Task StartAsync_AllowAnonymous_AuditsThatTheRouteIsUnauthenticated()
    {
        var node = CreateNode(allowAnonymous: true);

        await node.StartAsync(_triggerContext);

        var entry = Assert.Single(_eventService.Events);
        Assert.Equal(RtEventLevelsEnum.Information, entry.Level);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Contains("Route POST /webhook registered without authentication", entry.Message);
    }

    [Fact]
    public async Task StartAsync_SecuredRoute_AuditsNothingAtRegistration()
    {
        var node = CreateNode(allowAnonymous: false);

        await node.StartAsync(_triggerContext);

        Assert.Empty(_eventService.Events);
    }

    /// <remarks>
    /// AB#5031: the caller's raw bearer token has to reach <see cref="ExecutePipelineOptions" />, the
    /// per-execution side channel the delegation ("on-behalf-of") path in <c>AnthropicAiQuery@1</c>
    /// reads as <c>subject_token</c>. Without this pass-through the token is collected by
    /// <c>HttpRequestService</c> and then dropped, and every delegating node fails closed.
    /// It must NOT travel in the pipeline data: that root is echoed back in the response,
    /// persistable by <c>SetPipelineExecutionResult@1</c> and shown in the Studio debug panel.
    /// </remarks>
    [Fact]
    public async Task StartAsync_AuthenticatedCaller_ForwardsTheRawTokenOnTheOptionsOnly()
    {
        const string rawToken = "ey.caller.token";

        var captured = CaptureExecuteOptions();
        var node = CreateNode(allowAnonymous: false);
        var context = CreateHttpContext();
        context.User = CreateAuthenticatedUser("TenantAdmin");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";

        await node.StartAsync(_triggerContext);
        Assert.True(await _httpRequestService.SendRequestAsync(context));

        Assert.NotNull(captured.Options);
        Assert.Equal(rawToken, captured.Options!.CallerAccessToken);
        Assert.Equal("660000000000000000000042", captured.Options.VerifiedPrincipal?.SubjectId);

        // Negative: nowhere in the data root the pipeline (and the HTTP response) sees.
        Assert.NotNull(captured.Input);
        Assert.DoesNotContain(rawToken, captured.Input!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_AnonymousRoute_LeavesTheCallerAccessTokenUnset()
    {
        var captured = CaptureExecuteOptions();
        var node = CreateNode(allowAnonymous: true);

        await node.StartAsync(_triggerContext);
        Assert.True(await _httpRequestService.SendRequestAsync(CreateHttpContext()));

        Assert.NotNull(captured.Options);
        Assert.Null(captured.Options!.CallerAccessToken);
        Assert.Null(captured.Options.VerifiedPrincipal);
    }

    private CapturedExecution CaptureExecuteOptions()
    {
        var captured = new CapturedExecution();
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call =>
            {
                captured.Options = (ExecutePipelineOptions)call.Arguments[0]!;
                captured.Input = call.Arguments[1] as JsonNode;
            })
            .Returns(Task.FromResult<object?>(null));
        return captured;
    }

    private sealed class CapturedExecution
    {
        public ExecutePipelineOptions? Options { get; set; }
        public JsonNode? Input { get; set; }
    }

    [Fact]
    public async Task StopAsync_RemovesTheRoute()
    {
        var node = CreateNode(allowAnonymous: true);
        await node.StartAsync(_triggerContext);

        await node.StopAsync(_triggerContext);

        Assert.False(await _httpRequestService.SendRequestAsync(CreateHttpContext()));
    }
}

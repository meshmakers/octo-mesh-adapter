using System.Text.Json.Nodes;
using FakeItEasy;
using MassTransit;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Microsoft.Extensions.Logging.Abstractions;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
///     AB#10015: the dry run of an HTTP-triggered pipeline. Pins the invariant "a caller who could not call the route
///     cannot dry-run it, and nothing but a dry run comes in this way" with one test per abuse case, and that an allowed
///     dry run starts with the input as request body and IsDryRun set.
/// </summary>
public class HttpTriggerDryRunTests
{
    private const string TenantId = "dr10015";
    private const string PipelineRtId = "084930000000000000000002";

    private readonly ITriggerContext _context = A.Fake<ITriggerContext>();
    private readonly List<ExecutePipelineResponse> _responses = [];
    private ExecutePipelineOptions? _startedOptions;
    private object? _startedInput;
    private readonly Guid _executionId = Guid.NewGuid();

    public HttpTriggerDryRunTests()
    {
        A.CallTo(() => _context.TenantId).Returns(TenantId);
        A.CallTo(() => _context.NodeContext).Returns(A.Fake<INodeContext>());
        A.CallTo(() => _context.PipelineRtEntityId)
            .Returns(new RtEntityId("System.Communication/Pipeline-1", new OctoObjectId(PipelineRtId)));
        A.CallTo(() => _context.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes((ExecutePipelineOptions o, object? i) =>
            {
                _startedOptions = o;
                _startedInput = i;
            })
            .Returns(_executionId);
        A.CallTo(() => _context.EndExecutePipelineAsync(A<Guid>._)).Returns(Task.FromResult<object?>(null));
    }

    private static FromHttpRequestNodeConfiguration2 Route(bool allowAnonymous = false, params string[] roles) =>
        new() { Method = HttpMethod.Post, Path = "/maintenance-tickets", AllowAnonymous = allowAnonymous, RequiredRoles = roles };

    private static ExecutePipelineCaller Caller(string? tenantId = TenantId, params string[] roles) =>
        new() { SubjectId = "660000000000000000000042", TenantId = tenantId, Name = "Gerald", Roles = roles, TrustLevel = 2 };

    private static ExecutePipelineRequest Request(bool isDryRun, ExecutePipelineCaller? caller, string? input = null) =>
        new(TenantId, input) { IsDryRun = isDryRun, Caller = caller };

    private Task Handle(FromHttpRequestNodeConfiguration2 route, ExecutePipelineRequest request) =>
        HttpTriggerDryRun.HandleAsync(_context, route, request, r =>
        {
            _responses.Add((ExecutePipelineResponse)r);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task RealExecute_IsRefused_AndThePipelineDoesNotStart()
    {
        await Handle(Route(), Request(isDryRun: false, Caller()));

        var response = Assert.Single(_responses);
        Assert.False(response.IsSuccessStartingExecution);
        Assert.Contains("only as a dry run", response.ErrorMessage);
        A.CallTo(() => _context.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task DryRun_WithoutTheRequiredRole_IsRefused()
    {
        await Handle(Route(false, "MaintenanceManagement"), Request(true, Caller(TenantId, "Reader")));

        var response = Assert.Single(_responses);
        Assert.False(response.IsSuccessStartingExecution);
        Assert.Contains("MaintenanceManagement", response.ErrorMessage);
        A.CallTo(() => _context.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task DryRun_WithoutACarriedCaller_IsRefused_AlsoForAnAnonymousRoute()
    {
        await Handle(Route(allowAnonymous: true), Request(true, caller: null));

        Assert.False(Assert.Single(_responses).IsSuccessStartingExecution);
        A.CallTo(() => _context.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("octosystem")]
    [InlineData("")]
    [InlineData(null)]
    public void DryRun_ByATokenOfAnotherOrNoTenant_IsRefused(string? callerTenant)
    {
        var refusal = HttpTriggerDryRun.Authorize(Request(true, Caller(callerTenant)), TenantId, Route());

        Assert.NotNull(refusal);
        Assert.Contains("does not serve", refusal);
    }

    [Fact]
    public void RoleCheck_IsOrdinal_AndSkipsBlankEntries()
    {
        Assert.NotNull(HttpTriggerDryRun.Authorize(Request(true, Caller(TenantId, "maintenancemanagement")), TenantId,
            Route(false, "MaintenanceManagement")));
        Assert.NotNull(HttpTriggerDryRun.Authorize(Request(true, Caller(TenantId, "")), TenantId, Route(false, "", "X")));
        Assert.Null(HttpTriggerDryRun.Authorize(Request(true, Caller(TenantId)), TenantId, Route(false, " ")));
    }

    [Fact]
    public void TenantMatch_IsCaseInsensitive_AsOnTheRoute()
    {
        Assert.Null(HttpTriggerDryRun.Authorize(Request(true, Caller("DR10015")), TenantId, Route()));
    }

    [Fact]
    public async Task AllowedDryRun_StartsWithTheInputAsBody_AsDryRun_AndAnswersTheExecutionId()
    {
        await Handle(Route(false, "MaintenanceManagement", "TenantAdmin"),
            Request(true, Caller(TenantId, "TenantAdmin"), """{"title":"Pump leaks","priority":1}"""));

        var response = Assert.Single(_responses);
        Assert.True(response.IsSuccessStartingExecution);
        Assert.Equal(_executionId, response.PipelineExecutionId);

        Assert.NotNull(_startedOptions);
        Assert.True(_startedOptions!.IsDryRun);
        Assert.Equal(PipelineTriggerType.Manual, _startedOptions.TriggerType);
        Assert.Equal("660000000000000000000042", _startedOptions.VerifiedPrincipal?.SubjectId);

        var input = Assert.IsType<JsonObject>(_startedInput);
        Assert.Equal("Pump leaks", input["body"]?["title"]?.GetValue<string>());
        Assert.Equal("POST", input["method"]?.GetValue<string>());
        Assert.Equal("/maintenance-tickets", input["path"]?.GetValue<string>());
        Assert.Equal(TenantId, input["principal"]?["tenantId"]?.GetValue<string>());
    }

    [Fact]
    public async Task FailedStart_IsAnsweredWithTheMessage()
    {
        A.CallTo(() => _context.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .ThrowsAsync(new InvalidOperationException("not registered"));

        await Handle(Route(), Request(true, Caller()));

        var response = Assert.Single(_responses);
        Assert.False(response.IsSuccessStartingExecution);
        Assert.Equal("not registered", response.ErrorMessage);
    }

    [Fact]
    public void BuildInput_TextIsKeptAsString_EmptyInputHasNoBody()
    {
        var principal = new VerifiedPrincipal("s", TenantId, null, null, ["R"]);

        Assert.Equal("plain text", HttpTriggerDryRun.BuildInput("plain text", principal, Route())["body"]?.GetValue<string>());
        Assert.False(HttpTriggerDryRun.BuildInput("  ", principal, Route()).ContainsKey("body"));
        Assert.Equal("R", HttpTriggerDryRun.BuildInput(null, principal, Route())["principal"]?["roles"]?[0]?.GetValue<string>());
    }

    [Fact]
    public void Address_IsTheExecuteAddressPlusTheSuffix()
    {
        Assert.Equal("octo::com-controller::execute-pipeline-dr10015-pipeline-084930000000000000000002-http-dry-run",
            HttpTriggerDryRun.GetAddress("DR10015", PipelineRtId));
    }

    [Fact]
    public async Task Endpoints_OnePerPipeline_ReleasedOnDispose()
    {
        var eventHub = A.Fake<IEventHubControl>();
        A.CallTo(() => eventHub.RegisterCommandConsumer(A<string>._, A<ExecuteCommandHandler<ExecutePipelineRequest>>._,
                A<CommandConsumerOptions>._))
            .ReturnsLazily(() => new EndpointHandle(A.Fake<HostReceiveEndpointHandle>()));
        var endpoints = new HttpTriggerDryRunEndpoints(eventHub, NullLogger<HttpTriggerDryRunEndpoints>.Instance);

        var first = endpoints.Register(_context, Route());
        var second = endpoints.Register(_context, Route());

        Assert.NotNull(first);
        Assert.Null(second);
        A.CallTo(() => eventHub.RegisterCommandConsumer(
                HttpTriggerDryRun.GetAddress(TenantId, PipelineRtId),
                A<ExecuteCommandHandler<ExecutePipelineRequest>>._,
                A<CommandConsumerOptions>.That.Matches(o => o.ExclusiveByDesignReason != null)))
            .MustHaveHappenedOnceExactly();

        await first!.DisposeAsync();
        Assert.NotNull(endpoints.Register(_context, Route()));
    }

    [Fact]
    public async Task Node_RegistersTheDryRunEndpoint_AndRemovesItOnStop()
    {
        var endpoints = A.Fake<IHttpTriggerDryRunEndpoints>();
        var registration = A.Fake<IAsyncDisposable>();
        A.CallTo(() => endpoints.Register(_context, A<FromHttpRequestNodeConfiguration2>._)).Returns(registration);
        A.CallTo(() => _context.NodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration2>()).Returns(Route());
        var http = A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.IHttpRequestService>();
        var node = new FromHttpRequestNode2(NullLogger<FromHttpRequestNode2>.Instance, http,
            A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.Services.IAdapterEventService>(), endpoints);

        await node.StartAsync(_context);
        await node.StopAsync(_context);

        A.CallTo(() => endpoints.Register(_context, A<FromHttpRequestNodeConfiguration2>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => registration.DisposeAsync()).MustHaveHappenedOnceExactly();
    }
}

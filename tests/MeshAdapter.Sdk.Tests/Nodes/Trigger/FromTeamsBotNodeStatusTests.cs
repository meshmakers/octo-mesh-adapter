using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5620: FromTeamsBot@1 reports a status line per handled activity (throttled to one per 30 s),
/// and an error line when the handler throws or the inbound Bot Framework token is rejected.
/// </summary>
public class FromTeamsBotNodeStatusTests
{
    private sealed record Report(string Message, bool IsError);

    private sealed class Harness
    {
        public required FromTeamsBotNode Node { get; init; }
        public required ITriggerContext Context { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public List<Report> Reports { get; } = [];
        public HttpRequestOptions? Route { get; set; }

        public Task PostAsync(string? authorization = null)
        {
            var activity = new JsonObject
            {
                ["type"] = "message",
                ["id"] = "act-1",
                ["text"] = "hello",
                ["from"] = new JsonObject { ["id"] = "29:user", ["name"] = "User", ["aadObjectId"] = "oid-1" },
                ["conversation"] = new JsonObject { ["id"] = "conv-1" },
                ["serviceUrl"] = "https://smba.trafficmanager.net/emea/"
            };
            var headers = new JsonObject();
            if (authorization != null)
            {
                headers["Authorization"] = authorization;
            }

            return Route!.ExecuteFunc(new JsonObject { ["body"] = activity, ["headers"] = headers },
                TriggerCallerContext.Anonymous);
        }
    }

    private static async Task<Harness> StartAsync(bool validateInboundToken, bool executeThrows = false)
    {
        var config = new FromTeamsBotNodeConfiguration
        {
            ServerConfiguration = "graph", BotAppId = "bot-app", ValidateInboundToken = validateInboundToken
        };
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromTeamsBotNodeConfiguration>()).Returns(config);
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.IsDefined("graph")).Returns(true);

        var context = A.Fake<ITriggerContext>();
        A.CallTo(() => context.NodeContext).Returns(nodeContext);
        A.CallTo(() => context.GlobalConfiguration).Returns(globalConfig);
        if (executeThrows)
        {
            A.CallTo(context).Where(call => call.Method.Name == nameof(ITriggerContext.ExecuteAsync))
                .WithReturnType<Task<object?>>()
                .Throws(new InvalidOperationException("pipeline exploded"));
        }

        var binder = A.Fake<IChannelCallerBinder>();
        A.CallTo(binder).Where(call => call.Method.Name == nameof(IChannelCallerBinder.BindAsync))
            .WithReturnType<Task<ChannelBindingResult>>()
            .Returns(ChannelBindingResult.Anonymous);

        var httpRequestService = A.Fake<IHttpRequestService>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 4, 20, 0, TimeSpan.Zero));
        var harness = new Harness
        {
            Node = new FromTeamsBotNode(NullLogger<FromTeamsBotNode>.Instance, httpRequestService,
                A.Fake<IHttpClientFactory>(), binder) { Clock = clock },
            Context = context,
            Clock = clock
        };
        A.CallTo(() => httpRequestService.CreateRoute(A<HttpRequestOptions>._))
            .ReturnsLazily((HttpRequestOptions o) =>
            {
                harness.Route = o;
                return new HttpRouteHandle(httpRequestService, o);
            });
        A.CallTo(() => context.ReportStatusAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Invokes((string message, bool isError, CancellationToken _) => harness.Reports.Add(new Report(message, isError)))
            .Returns(Task.CompletedTask);

        await harness.Node.StartAsync(context);
        return harness;
    }

    [Fact]
    public async Task HandledActivity_ReportsLastActivityLine()
    {
        var h = await StartAsync(validateInboundToken: false);

        await h.PostAsync();

        var report = Assert.Single(h.Reports);
        Assert.False(report.IsError);
        Assert.Equal("2026-10-07T04:20:00Z · /teamsBot · last activity handled, 0 attachment(s)", report.Message);
    }

    [Fact]
    public async Task ActivityBurst_ReportsAtMostOncePer30Seconds()
    {
        var h = await StartAsync(validateInboundToken: false);

        await h.PostAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.PostAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.PostAsync();
        Assert.Single(h.Reports);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.PostAsync();
        Assert.Equal(2, h.Reports.Count);
    }

    [Fact]
    public async Task RejectedToken_ReportsError_WithoutTokenContents()
    {
        var h = await StartAsync(validateInboundToken: true);

        await h.PostAsync(authorization: null);

        var report = Assert.Single(h.Reports);
        Assert.True(report.IsError);
        Assert.Equal(
            "ERROR 2026-10-07T04:20:00Z · /teamsBot · inbound activity rejected: missing or non-Bearer Authorization header",
            report.Message);
    }

    [Fact]
    public async Task HandlerThrows_ReportsTheExceptionAsError()
    {
        var h = await StartAsync(validateInboundToken: false, executeThrows: true);

        await h.PostAsync();

        var error = Assert.Single(h.Reports);
        Assert.True(error.IsError);
        Assert.EndsWith(" · /teamsBot · pipeline exploded", error.Message);
    }

    [Fact]
    public async Task RepeatedRejectedTokens_Within30Seconds_ReportOnce()
    {
        // The route is public: a stream of bad tokens must not become a stream of writes.
        var h = await StartAsync(validateInboundToken: true);
        await h.PostAsync(authorization: null);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.PostAsync(authorization: null);

        Assert.Single(h.Reports);
    }
}

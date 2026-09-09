using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5145: FromSignal@1 with NO Signal endpoint (no registered SignalChannel, no legacy
/// settings) stays IDLE — StartAsync neither throws (the old behaviour, which flipped the
/// pipeline into a deploy error) nor starts the polling loop; teardown stays clean. With the
/// tenant's SignalChannel projected into the GlobalConfiguration, the trigger starts polling
/// without any node-level connection configuration at all (the new-style pipeline shape).
/// </summary>
public class FromSignalNodeResolutionTests
{
    private const string RegisteredChannelJson =
        """
        {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
         "RegistrationState":2}}
        """;

    private static ITriggerContext BuildContext(
        FromSignalNodeConfiguration config, string? signalChannelJson)
    {
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromSignalNodeConfiguration>())
            .Returns(config);

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        if (signalChannelJson != null)
        {
            A.CallTo(() => globalConfig.IsDefined("signal-channel")).Returns(true);
            A.CallTo(() => globalConfig.GetRawJson("signal-channel")).Returns(signalChannelJson);
        }

        var context = A.Fake<ITriggerContext>();
        A.CallTo(() => context.NodeContext).Returns(nodeContext);
        A.CallTo(() => context.GlobalConfiguration).Returns(globalConfig);
        return context;
    }

    [Fact]
    public async Task NoEndpointConfigured_TriggerStaysIdle_NoThrowNoPolling()
    {
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        var node = new FromSignalNode(NullLogger<FromSignalNode>.Instance, httpClientFactory,
            A.Fake<IChannelCallerBinder>());
        // A new-style pipeline definition: no connection/settings properties at all.
        var context = BuildContext(new FromSignalNodeConfiguration(), signalChannelJson: null);

        var startException = await Record.ExceptionAsync(() => node.StartAsync(context));
        Assert.Null(startException);

        // Idle means idle: the polling loop was never started, so no HTTP client was created.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).MustNotHaveHappened();

        var stopException = await Record.ExceptionAsync(() => node.StopAsync(context));
        Assert.Null(stopException);
    }

    [Fact]
    public async Task NotRegisteredChannel_WithoutLegacy_TriggerStaysIdle()
    {
        const string codePendingJson =
            """
            {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
             "RegistrationState":1}}
            """;
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        var node = new FromSignalNode(NullLogger<FromSignalNode>.Instance, httpClientFactory,
            A.Fake<IChannelCallerBinder>());
        var context = BuildContext(new FromSignalNodeConfiguration(), codePendingJson);

        await node.StartAsync(context);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).MustNotHaveHappened();

        await node.StopAsync(context);
    }

    [Fact]
    public async Task RegisteredChannel_StartsPollingWithoutAnyNodeConfiguration()
    {
        // The channel entry alone (the entry the controller now always ships) makes the trigger
        // start — throwing on the missing node properties would defeat the new-style pipelines.
        // The handler fails every request so the poll loop parks in its error backoff instead of
        // processing anything.
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(new ThrowingHandler(), disposeHandler: false));
        var node = new FromSignalNode(NullLogger<FromSignalNode>.Instance, httpClientFactory,
            A.Fake<IChannelCallerBinder>());
        var context = BuildContext(new FromSignalNodeConfiguration(), RegisteredChannelJson);

        await node.StartAsync(context);

        // The polling task ran far enough to ask for its HTTP client — the loop is alive.
        await WaitUntilAsync(() => Fake.GetCalls(httpClientFactory)
            .Any(call => call.Method.Name == nameof(IHttpClientFactory.CreateClient)));

        await node.StopAsync(context);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("simulated bridge outage");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Condition was not reached within the wait budget.");
    }
}

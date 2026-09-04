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
/// AB#4761: when the Graph mailbox poll errors, the loop enters a 30s backoff
/// <c>Task.Delay(..., token)</c>. A <see cref="FromMicrosoftGraphEmailNode.StopAsync"/> cancels
/// that token, so the delay throws — and because it is raised from inside the poll loop's
/// <c>catch (Exception)</c>, the sibling <c>catch (OperationCanceledException)</c> does not cover it.
/// Before the fix the polling task faulted and <c>StopAsync().WaitAsync()</c> rethrew, which failed
/// trigger unregistration and flipped the whole adapter to ConfigurationState=Error on the next
/// config reconcile. These tests drive the real node lifecycle into that exact backoff window and
/// assert the teardown stays clean.
/// </summary>
public class FromMicrosoftGraphEmailNodeLifecycleTests
{
    private static ITriggerContext BuildContext()
    {
        var config = new FromMicrosoftGraphEmailNodeConfiguration
        {
            ServerConfiguration = "graph",
            Mailbox = "invoices@example.com",
            FolderPath = "Archive/Invoices/ToDo",
            PollingIntervalSeconds = 1
        };

        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromMicrosoftGraphEmailNodeConfiguration>())
            .Returns(config);

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(true);

        var context = A.Fake<ITriggerContext>();
        A.CallTo(() => context.NodeContext).Returns(nodeContext);
        A.CallTo(() => context.GlobalConfiguration).Returns(globalConfig);
        return context;
    }

    [Fact]
    public async Task StopAsync_WhilePollingTaskIsInErrorBackoff_DoesNotThrow()
    {
        // The HTTP client creation throws, so the very first poll iteration fails and the loop
        // parks in the 30s error-backoff Task.Delay — the window the regression is about.
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .Throws(new HttpRequestException("simulated Graph outage"));
        var logger = NullLogger<FromMicrosoftGraphEmailNode>.Instance;

        var node = new FromMicrosoftGraphEmailNode(logger, httpClientFactory, A.Fake<IChannelCallerBinder>());
        var context = BuildContext();

        await node.StartAsync(context);

        // Give the background poll task a moment to fail and reach the backoff delay.
        await Task.Delay(300);

        // Cancelling the backoff delay must not fault the poll task nor make teardown throw.
        var exception = await Record.ExceptionAsync(() => node.StopAsync(context));
        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_ImmediatelyAfterStart_DoesNotThrow()
    {
        // Racing StopAsync right after StartAsync (cancel before the loop reaches the delay) must
        // also tear down cleanly — no faulted task, no rethrow.
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .Throws(new HttpRequestException("simulated Graph outage"));
        var logger = NullLogger<FromMicrosoftGraphEmailNode>.Instance;

        var node = new FromMicrosoftGraphEmailNode(logger, httpClientFactory, A.Fake<IChannelCallerBinder>());
        var context = BuildContext();

        await node.StartAsync(context);

        var exception = await Record.ExceptionAsync(() => node.StopAsync(context));
        Assert.Null(exception);
    }
}

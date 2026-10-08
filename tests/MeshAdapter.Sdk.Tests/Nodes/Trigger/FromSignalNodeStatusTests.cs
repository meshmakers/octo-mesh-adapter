using System.Net;
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
/// AB#5619: FromSignal@1 reports its receive status through ITriggerContext.ReportStatusAsync —
/// throttled, so a tight poll loop does NOT write one status per poll.
/// </summary>
public class FromSignalNodeStatusTests
{
    private const string Number = "+43677111111111";

    private sealed record Report(string Message, bool IsError);

    private static (ITriggerContext Context, List<Report> Reports) BuildContext(FromSignalNodeConfiguration config)
    {
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromSignalNodeConfiguration>()).Returns(config);
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        var reports = new List<Report>();
        var context = A.Fake<ITriggerContext>();
        A.CallTo(() => context.NodeContext).Returns(nodeContext);
        A.CallTo(() => context.GlobalConfiguration).Returns(globalConfig);
        A.CallTo(() => context.ReportStatusAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Invokes((string message, bool isError, CancellationToken _) =>
            {
                lock (reports)
                {
                    reports.Add(new Report(message, isError));
                }
            })
            .Returns(Task.CompletedTask);
        return (context, reports);
    }

    private static FromSignalNode BuildNode(HttpMessageHandler handler)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));
        return new FromSignalNode(NullLogger<FromSignalNode>.Instance, factory, A.Fake<IChannelCallerBinder>());
    }

    private static FromSignalNodeConfiguration LegacyConfig() => new()
    {
        ApiUrl = "http://bridge.signal:8080", Number = Number, PollingIntervalSeconds = 0
    };

    [Fact]
    public async Task IdlePolls_ReportOnce_NotPerPoll()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var node = BuildNode(handler);
        var (context, reports) = BuildContext(LegacyConfig());

        await node.StartAsync(context);
        await WaitUntilAsync(() => handler.Calls >= 20);
        await node.StopAsync(context);

        var report = Assert.Single(reports);
        Assert.False(report.IsError);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ · \+43677111111111 · received 0, processed 0, rejected 0$",
            report.Message);
    }

    [Fact]
    public async Task ReceiptsAndSenderFilteredMessages_AreCountedAndReported()
    {
        const string body =
            """
            [{"envelope":{"sourceNumber":"+111","receiptMessage":{}}},
             {"envelope":{"sourceNumber":"+999","dataMessage":{"message":"hi"}}}]
            """;
        var first = true;
        var handler = new CountingHandler(_ =>
        {
            var content = first ? body : "[]";
            first = false;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });
        var node = BuildNode(handler);
        var config = LegacyConfig() with { SenderFilter = "+43" };
        var (context, reports) = BuildContext(config);

        await node.StartAsync(context);
        await WaitUntilAsync(() => handler.Calls >= 5);
        await node.StopAsync(context);

        // The first poll is both "first event" and "activity": one line, the receipt not counted.
        var report = Assert.Single(reports);
        Assert.EndsWith(" · received 1, processed 0, rejected 1", report.Message);
    }

    [Fact]
    public async Task BridgeOutage_IsReportedAsError()
    {
        var handler = new CountingHandler(_ => throw new HttpRequestException("Connection refused (bridge.signal:8080)"));
        var node = BuildNode(handler);
        var (context, reports) = BuildContext(LegacyConfig());

        await node.StartAsync(context);
        await WaitUntilAsync(() => { lock (reports) { return reports.Count > 0; } });
        await node.StopAsync(context);

        var report = Assert.Single(reports);
        Assert.True(report.IsError);
        Assert.StartsWith("ERROR ", report.Message);
        Assert.EndsWith(" · +43677111111111 · Connection refused (bridge.signal:8080)", report.Message);
    }

    [Fact]
    public async Task HttpTimeout_IsAFailedPoll_NotTheEndOfPolling()
    {
        // HttpClient reports its own timeout as TaskCanceledException; before AB#5619 that ended
        // the poll loop for good without a trace on the pipeline.
        var handler = new CountingHandler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var node = BuildNode(handler);
        var (context, reports) = BuildContext(LegacyConfig());

        await node.StartAsync(context);
        await WaitUntilAsync(() => { lock (reports) { return reports.Count > 0; } });
        await node.StopAsync(context);

        Assert.True(Assert.Single(reports).IsError);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250; i++)
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

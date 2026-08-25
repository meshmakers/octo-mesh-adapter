using System.Net;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class HttpRequestSenderTests : NodeTestBase
{
    private static readonly HttpRetryOptions FourAttempts = new() { MaxAttempts = 4, BackoffBaseSeconds = 1 };

    private INodeContext NodeContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response"
        };
        var (_, nodeContext, _) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        return nodeContext;
    }

    private static Func<HttpRequestMessage> Get(string url = "https://host/api")
    {
        return () => new HttpRequestMessage(HttpMethod.Get, url);
    }

    /// <summary>
    /// Drives virtual time forward in small steps until the pending call finishes, so a test never
    /// waits on the wall clock. It releases whatever the sender is waiting for; it does not fix
    /// when the released continuation runs, so nothing may be asserted about the clock reading at
    /// the moment a request goes out.
    /// </summary>
    private static async Task<T> WithAdvancingTime<T>(FakeTimeProvider time, Task<T> pending)
    {
        while (!pending.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        return await pending;
    }

    /// <summary>
    /// Records every delay a caller asks the clock to time, so a backoff sequence can be pinned at
    /// its source instead of through the scheduling of the continuations it releases.
    /// </summary>
    private sealed class RecordingTimeProvider : FakeTimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
            TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SendAsync_TransientStatusThenSuccess_Succeeds(HttpStatusCode transient)
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(transient, "busy"),
            SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task SendAsync_TransientExceptionThenSuccess_Succeeds(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(exception),
            SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SendAsync_NonTransientStatus_FailsOnFirstAttempt(HttpStatusCode status)
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Status(status, "nope"));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task SendAsync_AttemptsExhausted_ReportsStatusAttemptsAndTruncatedBody()
    {
        var body = new string('x', 500);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, body));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(4, handler.CallCount);
        Assert.Contains("503", ex.Message);
        Assert.Contains("4 attempts", ex.Message);
        Assert.Contains(new string('x', 300), ex.Message);
        Assert.DoesNotContain(new string('x', 301), ex.Message);
    }

    [Fact]
    public async Task SendAsync_ExhaustedTimeouts_ThrowsTypedNotCancellation()
    {
        // ForEach@1 isolates every exception except OperationCanceledException, and
        // TaskCanceledException derives from it: a timeout that escaped raw would abort a whole
        // loop instead of failing one iteration.
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(new TaskCanceledException()));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, 5, time, NodeContext())));

        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task SendAsync_Backoff_WaitsAfterEachFailedAttempt()
    {
        // Parity with the fetch core being replaced: the wait happens AFTER a failed attempt and
        // doubles, so four attempts with a base of one second wait 1 s, 2 s, 4 s - and nothing is
        // waited for after the last one. Asserted on the delays the sender asks the clock for
        // rather than on the virtual time between handler calls: the clock is driven by polling,
        // so a continuation that reaches the handler a step late would report a gap that says
        // nothing about what was actually waited for. With no timeout configured these are the
        // only timers the sender creates.
        var time = new RecordingTimeProvider();
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(4, handler.CallCount);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            time.RequestedDelays);
    }

    [Fact]
    public async Task SendAsync_TargetNeverAnswers_TimesOutPerAttempt()
    {
        // The timeout itself, not a thrown TaskCanceledException standing in for it: the handler
        // accepts the request and never answers, so only the node's own cancellation source ends it.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Hangs());
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 },
                10, time, NodeContext())));

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task SendAsync_Backoff_IsCappedPerWait()
    {
        // Doubling is unbounded: with a base of one second the tenth wait would already be over
        // eight minutes, and a large base reaches the point where Task.Delay refuses the value and
        // throws out of the wait itself. Each wait is capped, so the doubling stops growing rather
        // than the run falling over.
        var time = new RecordingTimeProvider();
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler),
                Get(),
                new HttpRetryOptions { MaxAttempts = 8, BackoffBaseSeconds = 30 },
                null, time, NodeContext())));

        Assert.Equal(7, time.RequestedDelays.Count);
        Assert.All(time.RequestedDelays, d => Assert.True(
            d <= TimeSpan.FromSeconds(HttpRetryOptions.MaxBackoffSeconds),
            $"wait of {d} exceeds the cap"));
        // The first waits still double until they reach the cap, and stay there afterwards.
        Assert.Equal(TimeSpan.FromSeconds(30), time.RequestedDelays[0]);
        Assert.Equal(TimeSpan.FromSeconds(HttpRetryOptions.MaxBackoffSeconds), time.RequestedDelays[1]);
        Assert.Equal(TimeSpan.FromSeconds(HttpRetryOptions.MaxBackoffSeconds), time.RequestedDelays[6]);
    }

    [Fact]
    public async Task SendAsync_MaxAttemptsBeyondTheLimit_ThrowsBeforeAnyRequest()
    {
        // A configuration mistake, so it fails whatever the caller's error handling says.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => HttpRequestSender.SendAsync(new HttpClient(handler), Get(),
                new HttpRetryOptions { MaxAttempts = HttpRetryOptions.MaxAllowedAttempts + 1 },
                null, time, NodeContext()));

        Assert.Contains(HttpRetryOptions.MaxAllowedAttempts.ToString(), ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_NegativeBackoffBase_ThrowsBeforeAnyRequest()
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var time = new FakeTimeProvider();

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => HttpRequestSender.SendAsync(new HttpClient(handler), Get(),
                new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = -1 },
                null, time, NodeContext()));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_CancellationThatIsNotOurTimeout_DoesNotClaimTheConfiguredTimeout()
    {
        // The per-attempt timeout cannot undercut the HTTP client's own, which is process-wide:
        // configure 600 s against a client that gives up after 100 s and the client's timeout is
        // what ends the attempt. Reporting that as "exceeded the configured timeout of 600 s"
        // would send an operator looking for a setting that never took effect.
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(new TaskCanceledException()));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), new HttpRetryOptions { MaxAttempts = 1 },
                600, time, NodeContext())));

        Assert.DoesNotContain("600", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task SendAsync_DefaultOptions_MakesExactlyOneAttempt()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));
        var time = new FakeTimeProvider();

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), new HttpRetryOptions(), null, time, NodeContext())));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_ZeroOrNegativeAttempts_StillTriesOnce()
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), new HttpRetryOptions { MaxAttempts = 0 }, null, time,
            NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }
}

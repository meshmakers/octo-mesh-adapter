using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// The connection retry of <see cref="SshNetSftpSessionFactory" />. Most cases replace the connect
/// step to raise what a server makes SSH.NET raise; the last ones run SSH.NET's own connect against
/// loopback endpoints that close, reset or refuse every connection, and with an empty host.
/// </summary>
public class SshNetSftpSessionFactoryRetryTests : NodeTestBase
{
    private const string ServerConfig = "sftp-server-1";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly Dictionary<string, object?> _properties = new();
    private readonly List<string> _warnings = [];
    private readonly List<SftpClient> _clients = [];
    private readonly INodeContext _nodeContext;
    private int _calls;

    public SshNetSftpSessionFactoryRetryTests()
    {
        A.CallTo(() => _etlContext.Properties).Returns(_properties);
        var (_, nodeContext, _, logger) = PrepareTestWithLogger(new SftpListNodeConfiguration
        {
            ServerConfiguration = ServerConfig, RemoteDirectory = "/out", FilePattern = "*", TargetPath = "$.files"
        });
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .Invokes((string _, string _, string format, object[] args) =>
                _warnings.Add(string.Format(CultureInfo.InvariantCulture, format, args)));
        _nodeContext = nodeContext;
    }

    private static SftpConnectRetryOptions Retry(int maxAttempts, double backoffBaseSeconds) =>
        new() { MaxAttempts = maxAttempts, BackoffBaseSeconds = backoffBaseSeconds };

    private static SftpServerSettings Settings(SftpConnectRetryOptions? retry, string password = "secret") => new()
    {
        Host = "sftp.invalid", Username = "user", Password = password, MaxConcurrentConnections = 1,
        ConnectRetry = retry
    };

    private SemaphoreSlim Slot() =>
        ((ConcurrentDictionary<string, SemaphoreSlim>)_properties["SftpSessionFactory.Semaphores"]!)[ServerConfig];

    /// <summary>A factory whose connect step raises what <paramref name="failure" /> returns for the n-th call, or connects on null.</summary>
    private SshNetSftpSessionFactory Factory(TimeProvider time,
        Func<int, SshNetSftpSessionFactory.HostKeyOutcome, Exception?> failure, List<int>? freeSlots = null) =>
        new(time, (client, hostKey) =>
        {
            _clients.Add(client);
            freeSlots?.Add(Slot().CurrentCount);
            if (failure(Interlocked.Increment(ref _calls), hostKey) is { } exception)
            {
                throw exception;
            }
        });

    private Task<ISftpSession> Connect(SshNetSftpSessionFactory factory, SftpServerSettings settings,
        CancellationToken? cancellationToken = null) =>
        factory.ConnectAsync(settings, ServerConfig, _etlContext, _nodeContext,
            cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Drives virtual time until the call finishes, as HttpRequestSenderTests do; fails after 30 s of real time instead of hanging.</summary>
    private static async Task<T> WithAdvancingTime<T>(FakeTimeProvider time, Task<T> pending)
    {
        var elapsed = Stopwatch.StartNew();
        while (!pending.IsCompleted)
        {
            // A lost slot leaves the next attempt waiting for it on the real clock, whatever this one does.
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "The call did not finish within 30 s.");
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        return await pending;
    }

    /// <summary>Fails instead of hanging when the call waits on a clock the test does not move.</summary>
    private static Task<T> Bounded<T>(Task<T> pending) =>
        pending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    /// <summary>Records every delay asked of the clock, so the waits are pinned at their source, and runs <paramref name="onDelay" /> then.</summary>
    private sealed class RecordingTimeProvider(Action? onDelay = null) : FakeTimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            onDelay?.Invoke();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>What SSH.NET's <c>Connect</c> raises when the server refuses or drops the attempt that way.</summary>
    private static Exception? Refusal(string refusal) => refusal switch
    {
        "authentication refused" => new SshAuthenticationException("Permission denied (password)."),
        "subsystem refused" => new SshException("Subsystem 'sftp' could not be executed."),
        "channel closed" => new SshException("Channel was closed."),
        "connection reset" => new SocketException((int)SocketError.ConnectionReset),
        "connect timeout" => new SshOperationTimeoutException("Connection failed to establish within 30000 milliseconds."),
        // What a broken handshake can raise while parsing; unlike a blank host a passing failure.
        "argument out of range" => new ArgumentOutOfRangeException(nameof(refusal), "Specified argument was out of the range."),
        _ => null
    };

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task SingleAttempt_ConnectsOnce_WithoutWaitOrWarning_AndRaisesTheFailureAsItCame(int? maxAttempts)
    {
        var refused = new SshAuthenticationException("Permission denied (password).");
        var time = new RecordingTimeProvider();

        var ex = await Assert.ThrowsAsync<SshAuthenticationException>(() => Bounded(Connect(
            Factory(time, (_, _) => refused), Settings(maxAttempts is { } n ? Retry(n, 10) : null))));

        Assert.Same(refused, ex);
        Assert.Equal(1, _calls);
        Assert.Empty(time.RequestedDelays);
        Assert.Empty(_warnings);
        Assert.Equal(1, Slot().CurrentCount);
    }

    [Theory]
    [InlineData("nothing refused", "")]
    [InlineData("authentication refused", "SshAuthenticationException")]
    [InlineData("subsystem refused", "SshException")]
    [InlineData("channel closed", "SshException")]
    [InlineData("connection reset", "SocketException (ConnectionReset)")]
    [InlineData("connect timeout", "SshOperationTimeoutException")]
    [InlineData("argument out of range", "ArgumentOutOfRangeException")]
    public async Task ThreeAttempts_ConnectAtOnce_OrAfterTwoRefusals_WaitingAndWarningOnlyAfterARefusal(string refusal,
        string type)
    {
        var time = new RecordingTimeProvider();
        var factory = Factory(time, (call, _) => call < 3 ? Refusal(refusal) : null);

        var session = await WithAdvancingTime(time, Connect(factory, Settings(Retry(3, 10))));

        // Each refusal is repeated, its warning naming its type; connecting at once costs no wait, no warning.
        var refused = Refusal(refusal) is null ? 0 : 2;
        Assert.Equal(refused + 1, _calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) }.Take(refused), time.RequestedDelays);
        Assert.Equal(new[]
        {
            $"SFTP connection attempt 1 of 3 to server configuration 'sftp-server-1' failed with {type}; next attempt in 10 s",
            $"SFTP connection attempt 2 of 3 to server configuration 'sftp-server-1' failed with {type}; next attempt in 20 s"
        }.Take(refused), _warnings);
        // The open session holds the slot and hands it back once, however often it is disposed.
        Assert.Equal(0, Slot().CurrentCount);
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, Slot().CurrentCount);
    }

    public static TheoryData<int, double, double[], bool> Exhausted => new()
    {
        { 4, 1, [1, 2, 4], false },
        { 3, 0, [], false },
        { 10, double.MaxValue, [60, 60, 60, 60, 60, 60, 60, 60, 60], false },
        { 8, 30, [30, 60, 60, 60, 60, 60, 60], false },
        // Cancelled during the last attempt: no attempt is left to skip, so the call ends as exhausted.
        { 3, 1, [1, 2], true }
    };

    [Theory]
    [MemberData(nameof(Exhausted))]
    public async Task EveryAttemptFails_FailsWithTheLastFailure_AfterTheCappedWaits(int maxAttempts, double baseSeconds,
        double[] expectedWaits, bool cancelledDuringTheLastAttempt)
    {
        var time = new RecordingTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var factory = Factory(time, (call, _) =>
        {
            if (cancelledDuringTheLastAttempt && call == maxAttempts)
            {
                cancellation.Cancel();
            }

            return new SshException($"Channel was closed ({call}).");
        });

        var ex = await Assert.ThrowsAsync<SftpConnectFailedException>(() =>
            WithAdvancingTime(time, Connect(factory, Settings(Retry(maxAttempts, baseSeconds)), cancellation.Token)));

        Assert.Equal(maxAttempts, _calls);
        Assert.Equal(maxAttempts, ex.Attempts);
        Assert.Equal($"Channel was closed ({maxAttempts}).", ex.InnerException!.Message);
        Assert.Equal($"Connecting failed in all {maxAttempts} attempts the node's connectRetry allows; the last attempt failed with SshException: Channel was closed ({maxAttempts}).",
            ex.Message);
        // A wait after every attempt but the last, each at most 60 s; doubling the largest base
        // overflows to infinity, which the cap turns back into the cap.
        Assert.Equal(expectedWaits.Select(TimeSpan.FromSeconds), time.RequestedDelays);
        // One warning per failed attempt, never the failure text.
        Assert.Equal(maxAttempts, _warnings.Count);
        Assert.EndsWith($"attempt {maxAttempts} of {maxAttempts} to server configuration 'sftp-server-1' failed with SshException; no attempt left",
            _warnings[^1]);
        Assert.All(_warnings, w => Assert.DoesNotContain("Channel was closed", w));
        Assert.Equal(1, Slot().CurrentCount);
        // The client of every failed attempt is disposed: SSH.NET refuses to connect a disposed client.
        Assert.Equal(maxAttempts, _clients.Count);
        Assert.All(_clients, client => Assert.Throws<ObjectDisposedException>(() => client.Connect()));
    }

    [Theory]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(1.0, 7, 60.0)]
    [InlineData(10.0, 1, 10.0)]
    [InlineData(10.0, 2, 20.0)]
    [InlineData(45.0, 2, 60.0)]
    [InlineData(0.0, 5, 0.0)]
    public void WaitAfterFailedAttempt_IsBaseTimesTwoToTheNMinusOne_AtMostSixtySeconds(double baseSeconds,
        int failedAttempt, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            SshNetSftpSessionFactory.WaitAfterFailedAttempt(Retry(10, baseSeconds), failedAttempt));
        // Explicit nulls resolve to the defaults: one attempt, a base of one second.
        Assert.Equal(1, SshNetSftpSessionFactory.ResolveMaxAttempts(new SftpConnectRetryOptions { MaxAttempts = null }));
        Assert.Equal(TimeSpan.FromSeconds(2), SshNetSftpSessionFactory.WaitAfterFailedAttempt(
            new SftpConnectRetryOptions { BackoffBaseSeconds = null }, 2));
    }

    [Fact]
    public async Task FailedAttempts_EachHoldTheSlot_AndHandItBackBeforeTheWait()
    {
        var duringAttempts = new List<int>();
        var duringWaits = new List<int>();
        var time = new RecordingTimeProvider(() => duringWaits.Add(Slot().CurrentCount));

        await Assert.ThrowsAsync<SftpConnectFailedException>(() => WithAdvancingTime(time, Connect(
            Factory(time, (_, _) => new SshConnectionException("Connection reset."), duringAttempts),
            Settings(Retry(3, 1)))));

        // The only slot is taken during every attempt, free during both waits (one slot held
        // across all attempts would show 0 there) and free after the last attempt - not more: a
        // slot handed back twice would raise the limit for good.
        Assert.Equal(new[] { 0, 0, 0 }, duringAttempts);
        Assert.Equal(new[] { 1, 1 }, duringWaits);
        Assert.Equal(1, Slot().CurrentCount);
    }

    [Fact]
    public async Task WhileWaiting_TheSlotIsFree_AndACancellationStopsAtOnce()
    {
        var time = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();

        // The clock does not move, so the call sits in the wait after the first attempt.
        var pending = Connect(Factory(time, (_, _) => new SshConnectionException("Connection reset.")),
            Settings(Retry(3, 10)), cancellation.Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, Slot().CurrentCount);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(pending));
        Assert.Equal(1, _calls);
        Assert.Equal(1, Slot().CurrentCount);
    }

    [Fact]
    public async Task SlotWaitTimesOutOnALaterAttempt_IsNotRetried()
    {
        var time = new FakeTimeProvider();
        var pending = Connect(Factory(time, (_, _) => new SshConnectionException("Connection reset.")),
            Settings(Retry(3, 10)) with { WaitForSlotTimeoutSeconds = 1 });

        // Another execution takes the only slot while this one waits, so the second attempt runs
        // into the slot wait.
        Assert.True(Slot().Wait(0, TestContext.Current.CancellationToken));
        try
        {
            time.Advance(TimeSpan.FromSeconds(10));

            var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() => Bounded(pending));
            Assert.Contains("no free connection slot", ex.Message);
            Assert.Equal(1, _calls);
        }
        finally
        {
            Slot().Release();
        }
    }

    public static TheoryData<string> NotRetried =>
    [
        "refused host key", "refused host key, raised as another type", "refused host key, single attempt",
        "unreadable key material", "blank host", "no slots configured", "cancellation", "cancelled beforehand",
        "cancelled during a failing attempt", "cancelled during a failing attempt, its text carrying the password"
    ];

    [Theory]
    [MemberData(nameof(NotRetried))]
    public async Task NotRetried_FailsAtOnceWithoutAWait(string failure)
    {
        var time = new RecordingTimeProvider();
        var settings = Settings(Retry(3, 10));
        var token = TestContext.Current.CancellationToken;
        using var cancellation = new CancellationTokenSource();
        Exception? raised = null;
        var hostKeyRefused = failure.StartsWith("refused host key", StringComparison.Ordinal);
        if (hostKeyRefused)
        {
            settings = settings with { HostKeyFingerprint = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg" };
            raised = new SshConnectionException("Key exchange negotiation failed.");
        }

        switch (failure)
        {
            case "refused host key, raised as another type":
                raised = new SshException("Key exchange negotiation failed.");
                break;
            case "refused host key, single attempt":
                settings = settings with { ConnectRetry = null };
                break;
            case "unreadable key material":
                settings = settings with { Password = null, PrivateKey = "not-a-private-key", PrivateKeyPassphrase = "a-passphrase" };
                break;
            case "blank host":
                // The rule is the host, not the type: a failed name lookup is repeated against any other host.
                settings = settings with { Host = " " };
                raised = new SocketException((int)SocketError.HostNotFound);
                break;
            case "no slots configured":
                settings = settings with { MaxConcurrentConnections = 0 };
                break;
            case "cancellation":
                raised = new OperationCanceledException();
                break;
            case "cancelled beforehand":
                token = new CancellationToken(canceled: true);
                break;
            case "cancelled during a failing attempt":
                token = cancellation.Token;
                raised = new SshConnectionException("Connection reset.");
                break;
            case "cancelled during a failing attempt, its text carrying the password":
                token = cancellation.Token;
                raised = new SshConnectionException("Connection reset for password 'secret'.");
                break;
        }

        var factory = Factory(time, (_, hostKey) =>
        {
            // What the host key handler records before SSH.NET refuses the key exchange.
            hostKey.Presented = "2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo";
            hostKey.Refused = hostKeyRefused;
            // Only the two "cancelled during a failing attempt" rows pass this token: SSH.NET's Connect takes
            // none, so the attempt runs on past it.
            cancellation.Cancel();
            return raised;
        });

        var raisedAs = await Record.ExceptionAsync(() => Bounded(Connect(factory, settings, token)));

        // Each failure keeps its own type; a call that hangs fails here with the TimeoutException of Bounded.
        Exception ex = failure switch
        {
            "refused host key" or "refused host key, single attempt" or "no slots configured" =>
                Assert.IsType<MeshAdapterPipelineExecutionException>(raisedAs),
            "refused host key, raised as another type" or "unreadable key material" => Assert.IsType<SshException>(raisedAs),
            "blank host" => Assert.IsType<SocketException>(raisedAs),
            "cancelled beforehand" => Assert.IsAssignableFrom<OperationCanceledException>(raisedAs),
            _ => Assert.IsType<OperationCanceledException>(raisedAs)
        };
        Assert.Equal(raised is null ? 0 : 1, _calls);
        Assert.Empty(time.RequestedDelays);
        if (failure != "no slots configured")
        {
            Assert.Equal(1, Slot().CurrentCount);
        }

        // One warning per connection attempt, only with more than one attempt allowed, never announcing another.
        var warning = failure switch
        {
            "refused host key, single attempt" or "unreadable key material" or "no slots configured"
                or "cancelled beforehand" => null,
            _ when failure.StartsWith("cancelled during", StringComparison.Ordinal) =>
                "failed with SshConnectionException; cancelled, no further attempt",
            "refused host key" => "failed with SshConnectionException; this failure is not retried",
            "refused host key, raised as another type" => "failed with SshException; this failure is not retried",
            "blank host" => "failed with SocketException (HostNotFound); this failure is not retried",
            "cancellation" => "failed with OperationCanceledException; this failure is not retried",
            _ => throw new InvalidOperationException($"No expected warning for the row '{failure}'.")
        };
        if (warning is null)
        {
            Assert.Empty(_warnings);
        }
        else
        {
            Assert.EndsWith(warning, Assert.Single(_warnings));
        }

        if (failure is "refused host key" or "refused host key, single attempt")
        {
            Assert.Contains("does not match the configured fingerprint", ex.Message);
        }

        if (failure == "unreadable key material")
        {
            // Registered by the factory itself, so a caller that skipped the resolver is covered too.
            Assert.Equal("*** / ***", _nodeContext.SecretRegistry!.Redact("not-a-private-key / a-passphrase"));
        }

        if (failure == "cancelled during a failing attempt")
        {
            Assert.Same(raised, ex.InnerException);
        }

        if (failure.EndsWith("carrying the password", StringComparison.Ordinal))
        {
            // The registered password of the entry is masked in the failure the cancellation carries.
            Assert.Equal("Connection reset for password '***'.", ex.InnerException!.Message);
            Assert.DoesNotContain("secret", ex.InnerException!.ToString());
        }
    }

    [Theory]
    [InlineData("Pa55-word-in-the-text", "Permission denied for 'Pa55-word-in-the-text'.", "Permission denied for '***'.")]
    [InlineData("e", "Permission denied (password).", "Permission denied (password).")]
    [InlineData(" ", "Permission denied (password).", "Permission denied (password).")]
    public async Task FinalError_MasksThePassword_ByTheRuleOfTheSecretRegistry(string password, string failureText,
        string expectedText)
    {
        var refused = new SshAuthenticationException(failureText);
        var factory = Factory(new FakeTimeProvider(), (_, _) => refused);

        var ex = await Assert.ThrowsAsync<SftpConnectFailedException>(() =>
            Bounded(Connect(factory, Settings(Retry(2, 0), password))));

        // A value shorter than four characters is masked only where it is the whole text, a blank one never:
        // masked inside the text it would garble it and show where it matched.
        Assert.EndsWith($"SshAuthenticationException: {expectedText}", ex.Message);
        if (failureText == expectedText)
        {
            Assert.Same(refused, ex.InnerException);
        }
        else
        {
            Assert.Equal(expectedText, ex.InnerException!.Message);
            Assert.DoesNotContain(password, ex.InnerException!.ToString());
        }
    }

    /// <summary>Accepts loopback connections and closes or resets each one at once, before any server identification.</summary>
    private sealed class DroppingListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;

        public DroppingListener(bool reset)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                        Interlocked.Increment(ref _accepted);
                        socket.LingerState = new LingerOption(reset, 0);
                    }
                    catch (Exception) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }, _stop.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Theory]
    [InlineData("closes")]
    [InlineData("resets")]
    [InlineData("refuses")]
    public async Task RealConnect_EndpointThatDropsEveryConnection_IsRetriedUpToTheAttempts(string endpoint)
    {
        var listener = new DroppingListener(reset: endpoint == "resets");
        // Bound but never listening, for the whole test: a connection to it is refused at the socket
        // level, and no other process can take the port meanwhile.
        using var bound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        bound.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = endpoint == "refuses" ? ((IPEndPoint)bound.LocalEndPoint!).Port : listener.Port;

        try
        {
            var settings = Settings(Retry(3, 0)) with { Host = "127.0.0.1", Port = port, ConnectTimeoutSeconds = 30 };

            // Bounded: a slot that is not handed back after a failed attempt fails the test instead of hanging it.
            var ex = await Assert.ThrowsAsync<SftpConnectFailedException>(() =>
                Bounded(Connect(new SshNetSftpSessionFactory(TimeProvider.System), settings)));

            Assert.Equal(3, ex.Attempts);
            Assert.Equal(endpoint == "refuses" ? 0 : 3, listener.Accepted);
            Assert.True(ex.InnerException is SshConnectionException or SocketException, ex.InnerException?.GetType().Name);
            Assert.Contains($"the last attempt failed with {SshNetSftpSessionFactory.DescribeFailure(ex.InnerException!)}: ",
                ex.Message);
            // A dropped connection reads as lost before the banner or as a socket error, depending on
            // whether the close overtook the client's banner, which can differ from attempt to attempt.
            Assert.Equal(3, _warnings.Count);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Assert.Matches(endpoint == "refuses"
                    ? $@"^SFTP connection attempt {attempt} of 3 .* failed with SocketException \(ConnectionRefused\); "
                    : $@"^SFTP connection attempt {attempt} of 3 .* failed with (SshConnectionException|SocketException \(\w+\)); ",
                    _warnings[attempt - 1]);
            }
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task RealConnect_EmptyHost_FailsAtOnce_WithTheArgumentExceptionOfSshNet()
    {
        // SSH.NET builds the endpoint only while connecting, and an empty host name is refused there. With
        // a clock that does not move, a retry would wait for good and fail the bound instead.
        await Assert.ThrowsAsync<ArgumentException>(() => Bounded(Connect(
            new SshNetSftpSessionFactory(new FakeTimeProvider()), Settings(Retry(3, 10)) with { Host = "" })));
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// <c>connectRetry</c> against the real SFTP servers of <see cref="SftpServersFixture" />, through the
/// real <see cref="SshNetSftpSessionFactory" /> and <c>SftpList@1</c>. A TCP relay on the loopback
/// interface stands in front of the server: it closes the first connections it accepts before the
/// server could send a byte, forwards every later one and counts them all, so a test knows how many
/// connection attempts the node made.
/// </summary>
public sealed class SftpConnectRetryIntegrationTests(SftpServersFixture servers)
    : IClassFixture<SftpServersFixture>, IDisposable
{
    private readonly TcpListener _relay = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _warnings = [];
    private readonly List<DataContextImpl> _dataContexts = [];
    private int _accepted;

    public void Dispose()
    {
        _stop.Cancel();
        _relay.Stop();
        _dataContexts.ForEach(data => data.Dispose());
    }

    public static TheoryData<string> Servers => ["openssh", "sftpgo"];

    /// <summary>Starts the relay in front of <paramref name="server" /> and returns settings that connect through it.</summary>
    private SftpServerSettings ThroughRelay(string server, int dropFirst)
    {
        var direct = servers.Settings(server);
        _relay.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var incoming = await _relay.AcceptTcpClientAsync(_stop.Token);
                    if (Interlocked.Increment(ref _accepted) <= dropFirst)
                    {
                        incoming.Dispose();
                        continue;
                    }

                    _ = ForwardAsync(incoming, direct.Host, direct.Port);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }, _stop.Token);
        return direct with { Host = IPAddress.Loopback.ToString(), Port = ((IPEndPoint)_relay.LocalEndpoint).Port };
    }

    private async Task ForwardAsync(TcpClient incoming, string host, int port)
    {
        using (incoming)
        using (var outgoing = new TcpClient())
        {
            try
            {
                await outgoing.ConnectAsync(host, port, _stop.Token);
                await Task.WhenAny(incoming.GetStream().CopyToAsync(outgoing.GetStream(), _stop.Token),
                    outgoing.GetStream().CopyToAsync(incoming.GetStream(), _stop.Token));
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // One side went away or the relay is torn down; disposing both ends the other side.
            }
        }
    }

    private async Task<(Exception? Failure, DataContextImpl Data)> ListAsync(SftpServerSettings settings,
        string server, string filePattern, SftpConnectRetryOptions? retry)
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined("sftp")).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<SftpServerSettings>("sftp")).Returns(settings);
        var logger = A.Fake<IPipelineLogger>();
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .Invokes((string _, string _, string format, object[] args) =>
                _warnings.Add(string.Format(CultureInfo.InvariantCulture, format, args)));
        var data = new DataContextImpl(JsonDocument.Parse("{}"));
        _dataContexts.Add(data);
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger, data,
            null, null, null);

        try
        {
            await new SftpListNode(A.Fake<NodeDelegate>(), etlContext, new SshNetSftpSessionFactory())
                .ProcessObjectAsync(data, root.RegisterChildNode("SftpList", 0, new SftpListNodeConfiguration
                {
                    ServerConfiguration = "sftp", RemoteDirectory = SftpServersFixture.Directory(server),
                    FilePattern = filePattern, TargetPath = "$.files", ConnectRetry = retry
                }, data));
            return (null, data);
        }
        catch (Exception e)
        {
            return (e, data);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ServerDropsTheFirstConnection_TheNodeListsOnTheSecondAttempt(string server)
    {
        var name = $"RTRY{Guid.NewGuid():N}"[..20].ToUpperInvariant() + ".TXT";
        var direct = servers.Settings(server);
        using (var client = new SftpClient(direct.Host, direct.Port, SftpServersFixture.User, SftpServersFixture.Password))
        {
            client.Connect();
            client.UploadFile(new MemoryStream(Encoding.UTF8.GetBytes("retried")),
                $"{SftpServersFixture.Directory(server).TrimEnd('/')}/{name}", true);
        }

        var (failure, data) = await ListAsync(ThroughRelay(server, dropFirst: 1), server, name,
            new SftpConnectRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 });

        Assert.Null(failure);
        Assert.Equal(name, data.GetValue("$.files[0].name"));
        Assert.Equal(2, Volatile.Read(ref _accepted));
        Assert.StartsWith("SFTP connection attempt 1 of 3 to server configuration 'sftp' failed with ",
            Assert.Single(_warnings));
    }

    [Fact]
    public async Task ServerDropsTheFirstConnection_WithoutTheOption_TheNodeFailsAsBefore()
    {
        var (failure, _) = await ListAsync(ThroughRelay("openssh", dropFirst: 1), "openssh", "*", null);

        var ex = Assert.IsType<MeshAdapterPipelineExecutionException>(failure);
        Assert.Contains("Cannot list directory via SFTP: ", ex.Message);
        Assert.IsNotType<SftpConnectFailedException>(ex.InnerException);
        Assert.Equal(1, Volatile.Read(ref _accepted));
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task WrongPassword_IsRepeatedUntilTheAttemptsAreUsedUp()
    {
        const string wrongPassword = "not-the-password-of-the-account";

        var (failure, _) = await ListAsync(ThroughRelay("openssh", dropFirst: 0) with { Password = wrongPassword },
            "openssh", "*", new SftpConnectRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 });

        var ex = Assert.IsType<MeshAdapterPipelineExecutionException>(failure);
        Assert.Contains("Cannot list directory via SFTP: Connecting failed in all 2 attempts", ex.Message);
        Assert.Contains("SshAuthenticationException", ex.Message);
        Assert.IsType<SshAuthenticationException>(Assert.IsType<SftpConnectFailedException>(ex.InnerException).InnerException);
        Assert.Equal(2, Volatile.Read(ref _accepted));
        Assert.Equal(2, _warnings.Count);
    }

    [Fact]
    public async Task RefusedHostKey_IsNotRepeated()
    {
        // A well-formed fingerprint no test server presents.
        var (failure, _) = await ListAsync(
            ThroughRelay("openssh", dropFirst: 0) with { HostKeyFingerprint = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg" },
            "openssh", "*", new SftpConnectRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 });

        Assert.Contains("does not match the configured fingerprint",
            Assert.IsType<MeshAdapterPipelineExecutionException>(failure).Message);
        Assert.Equal(1, Volatile.Read(ref _accepted));
        Assert.EndsWith("this failure is not retried", Assert.Single(_warnings));
    }
}

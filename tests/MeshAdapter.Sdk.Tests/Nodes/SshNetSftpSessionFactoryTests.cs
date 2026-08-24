using System.Collections.Concurrent;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Only the guards that run before a socket is opened are unit tested here. Everything past
/// them needs a live SFTP server: the connection behaviour is covered by the SftpUpload node
/// tests running against a faked session, and by the staging verification.
/// </summary>
public class SshNetSftpSessionFactoryTests : NodeTestBase
{
    private INodeContext NodeContext()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = "sftp-server-1",
            RemoteDirectory = "/out",
            FileName = "x.txt",
            Path = "$.content"
        };
        var (_, nodeContext, _) = PrepareTest<SftpUploadNodeConfiguration>(config);
        return nodeContext;
    }

    private static SftpServerSettings Settings(int maxConcurrentConnections)
    {
        return new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            MaxConcurrentConnections = maxConcurrentConnections
        };
    }

    private static IMeshEtlContext EtlContext()
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        return etlContext;
    }

    [Fact]
    public async Task ConnectAsync_KeepsItsSemaphoresInTheEtlContext()
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        var properties = new Dictionary<string, object?>();
        A.CallTo(() => etlContext.Properties).Returns(properties);

        var factory = new SshNetSftpSessionFactory();

        // Connecting fails - there is no server - but the store has to exist by then, and it
        // has to live on the ETL context rather than on the factory, so the limit is scoped
        // the way every other node in this repository scopes it.
        await Assert.ThrowsAnyAsync<Exception>(
            () => factory.ConnectAsync(SettingsWithBrokenKey(), "sftp-server-1", etlContext, NodeContext()));

        Assert.True(properties.ContainsKey("SftpSessionFactory.Semaphores"));
        var semaphores = Assert.IsType<ConcurrentDictionary<string, SemaphoreSlim>>(
            properties["SftpSessionFactory.Semaphores"]);
        Assert.True(semaphores.ContainsKey("sftp-server-1"));
    }

    private static SftpServerSettings SettingsWithBrokenKey()
    {
        return new SftpServerSettings
        {
            Host = "sftp.invalid",
            Username = "user",
            // Not a key: parsing throws before any socket is opened.
            PrivateKey = "not-a-private-key",
            MaxConcurrentConnections = 1
        };
    }

    [Fact]
    public void EvaluateHostKey_MatchingFingerprint_TrustsAndRecordsWhatWasSeen()
    {
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            HostKeyFingerprint = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg"
        };
        var outcome = new SshNetSftpSessionFactory.HostKeyOutcome();

        var trusted = SshNetSftpSessionFactory.EvaluateHostKey(settings,
            "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg", outcome);

        Assert.True(trusted);
        Assert.False(outcome.Refused);
        Assert.Equal("kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg", outcome.Presented);
    }

    [Fact]
    public void EvaluateHostKey_DifferentFingerprint_RefusesAndRecordsWhatWasSeen()
    {
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            HostKeyFingerprint = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg"
        };
        var outcome = new SshNetSftpSessionFactory.HostKeyOutcome();

        var trusted = SshNetSftpSessionFactory.EvaluateHostKey(settings,
            "2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo", outcome);

        // Reporting false is what makes SSH.NET refuse; the presented key is kept so the
        // caller can name it once the refusal comes back as a connection failure.
        Assert.False(trusted);
        Assert.True(outcome.Refused);
        Assert.Equal("2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo", outcome.Presented);
    }

    [Fact]
    public void TranslateConnectFailure_AfterAHostKeyRefusal_NamesBothFingerprints()
    {
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            HostKeyFingerprint = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg"
        };
        var outcome = new SshNetSftpSessionFactory.HostKeyOutcome
        {
            Presented = "2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo",
            Refused = true
        };

        var translated = SshNetSftpSessionFactory.TranslateConnectFailure(
            new SshConnectionException("Host key could not be verified."), settings, outcome, NodeContext());

        Assert.IsType<MeshAdapterPipelineExecutionException>(translated);
        Assert.Contains("kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg", translated.Message);
        Assert.Contains("2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo", translated.Message);
    }

    [Fact]
    public void TranslateConnectFailure_OrdinaryConnectionFailure_IsLeftAlone()
    {
        var settings = new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" };
        var original = new SshConnectionException("Connection refused.");

        var translated = SshNetSftpSessionFactory.TranslateConnectFailure(original, settings,
            new SshNetSftpSessionFactory.HostKeyOutcome(), NodeContext());

        Assert.Same(original, translated);
    }

    [Fact]
    public void SftpHostKeyMismatch_NamesBothFingerprints()
    {
        const string expected = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg";
        const string presented = "2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo";

        var nodeContext = NodeContext();
        var ex = MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(nodeContext, "sftp.example.com", expected,
            presented);

        // An operator has to be able to tell a deliberately rotated key from the wrong server
        // without reproducing the connection by hand.
        Assert.Contains("sftp.example.com", ex.Message);
        Assert.Contains(expected, ex.Message);
        Assert.Contains(presented, ex.Message);
        // A flow may hold several SFTP nodes against the same server. Without the node path
        // the message names the host but not the step that reached it.
        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_SlotWaitTimesOut_NamesTheNodeThatWasWaiting()
    {
        var factory = new SshNetSftpSessionFactory();
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            MaxConcurrentConnections = 1,
            WaitForSlotTimeoutSeconds = 1
        };
        var etlContext = EtlContext();
        var nodeContext = NodeContext();

        // Take the only slot and never give it back, so the second caller runs into the wait.
        var semaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
        semaphores["sftp-server-1"] = new SemaphoreSlim(0, 1);
        etlContext.Properties["SftpSessionFactory.Semaphores"] = semaphores;

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => factory.ConnectAsync(settings, "sftp-server-1", etlContext, nodeContext));

        Assert.Contains("sftp-server-1", ex.Message);
        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_ClientCreationFails_ReleasesTheSlotForTheNextCaller()
    {
        var factory = new SshNetSftpSessionFactory();
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            // Not a key: PrivateKeyFile throws while the slot is already held.
            PrivateKey = "not-a-private-key",
            MaxConcurrentConnections = 1
        };

        // One context for both attempts: the counters live there, so a fresh one per call would
        // hand out a fresh slot and the test could never see a leak.
        var etlContext = EtlContext();

        await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(settings, "sftp-server-1", etlContext, NodeContext()));

        // With a leaked slot the second attempt would wait forever instead of failing.
        var second = factory.ConnectAsync(settings, "sftp-server-1", etlContext, NodeContext());
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, finished);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
    }

    [Fact]
    public void ReadCapped_ContentWithinTheCap_IsReturnedWhole()
    {
        var content = new byte[200_000];
        Random.Shared.NextBytes(content);

        var read = SshNetSftpSessionFactory.ReadCapped(new MemoryStream(content), "/x", content.Length, content.Length);

        // Larger than one chunk, so this also pins that the loop reassembles the pieces in order.
        Assert.Equal(content, read);
    }

    [Fact]
    public void ReadCapped_ContentExactlyAtTheCap_IsAccepted()
    {
        var content = new byte[1024];

        var read = SshNetSftpSessionFactory.ReadCapped(new MemoryStream(content), "/x", 1024, 1024);

        // The cap is what is still allowed, not the first value refused.
        Assert.Equal(1024, read.Length);
    }

    [Fact]
    public void ReadCapped_ContentPastTheCap_ThrowsWithoutReadingItAll()
    {
        var content = new byte[500_000];

        var ex = Assert.Throws<SftpFileTooLargeException>(
            () => SshNetSftpSessionFactory.ReadCapped(new MemoryStream(content), "/huge.bin", 100_000, 500_000));

        Assert.Equal("/huge.bin", ex.RemotePath);
        Assert.Equal(100_000, ex.MaxBytes);
        // No size: this is the file that outgrew what the server reported, so there is no
        // trustworthy number to put in the message.
        Assert.Null(ex.Size);
    }

    [Fact]
    public void ReadCapped_ServerUnderreportedTheSize_StillStopsAtTheCap()
    {
        var content = new byte[300_000];

        // The stat said 10 bytes and the stream delivers 300 000. Sizing the buffer from that
        // claim is fine; bounding the read by it would hand the remote side the decision.
        Assert.Throws<SftpFileTooLargeException>(
            () => SshNetSftpSessionFactory.ReadCapped(new MemoryStream(content), "/liar.bin", 100_000, 10));
    }

    [Fact]
    public void DisposeSession_DisconnectThrows_StillDisposesTheClientAndTheKeyMaterial()
    {
        var client = new RecordingDisposable();
        var privateKeyFile = new RecordingDisposable();
        var semaphore = new SemaphoreSlim(0, 1);

        SshNetSftpSessionFactory.DisposeSession(
            () => throw new SshConnectionException("The connection was closed by the server."),
            client, privateKeyFile, semaphore);

        Assert.True(client.Disposed);
        Assert.True(privateKeyFile.Disposed);
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public void DisposeSession_EveryTeardownStepThrows_SwallowsThemAllAndHandsTheSlotBack()
    {
        var privateKeyFile = new RecordingDisposable();
        var semaphore = new SemaphoreSlim(0, 1);

        // This runs while a using block unwinds, usually carrying the node failure the operator
        // has to read. A teardown that throws here would replace it, and the disposals after it
        // would never run.
        SshNetSftpSessionFactory.DisposeSession(
            () => throw new SshConnectionException("The connection was closed by the server."),
            new RecordingDisposable(true), privateKeyFile, semaphore);

        // The plaintext key material is released even though the client threw before it.
        Assert.True(privateKeyFile.Disposed);
        // A slot lost here is lost for the lifetime of the process.
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public void DisposeSession_WithoutKeyMaterial_StillHandsTheSlotBack()
    {
        var semaphore = new SemaphoreSlim(0, 1);

        SshNetSftpSessionFactory.DisposeSession(() => { }, new RecordingDisposable(), null, semaphore);

        Assert.Equal(1, semaphore.CurrentCount);
    }

    private sealed class RecordingDisposable(bool throwOnDispose = false) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("Teardown failed.");
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConnectAsync_NonPositiveMaxConcurrentConnections_ThrowsBeforeConnecting(int value)
    {
        var factory = new SshNetSftpSessionFactory();

        var nodeContext = NodeContext();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => factory.ConnectAsync(Settings(value), "sftp-server-1", EtlContext(), nodeContext));
        Assert.Contains("sftp-server-1", ex.Message);
        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
    }
}

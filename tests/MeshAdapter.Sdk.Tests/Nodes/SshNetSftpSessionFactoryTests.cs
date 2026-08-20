using System.Collections.Concurrent;
using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Only the guards that run before a socket is opened are unit tested here. Everything past
/// them needs a live SFTP server: the connection behaviour is covered by the SftpUpload node
/// tests running against a faked session, and by the staging verification.
/// </summary>
public class SshNetSftpSessionFactoryTests
{
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
            () => factory.ConnectAsync(SettingsWithBrokenKey(), "sftp-server-1", etlContext));

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
            new SshConnectionException("Host key could not be verified."), settings, outcome);

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
            new SshNetSftpSessionFactory.HostKeyOutcome());

        Assert.Same(original, translated);
    }

    [Fact]
    public void SftpHostKeyMismatch_NamesBothFingerprints()
    {
        const string expected = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg";
        const string presented = "2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo";

        var ex = MeshAdapterPipelineExecutionException.SftpHostKeyMismatch("sftp.example.com", expected, presented);

        // An operator has to be able to tell a deliberately rotated key from the wrong server
        // without reproducing the connection by hand.
        Assert.Contains("sftp.example.com", ex.Message);
        Assert.Contains(expected, ex.Message);
        Assert.Contains(presented, ex.Message);
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

        await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(settings, "sftp-server-1", etlContext));

        // With a leaked slot the second attempt would wait forever instead of failing.
        var second = factory.ConnectAsync(settings, "sftp-server-1", etlContext);
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, finished);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConnectAsync_NonPositiveMaxConcurrentConnections_ThrowsBeforeConnecting(int value)
    {
        var factory = new SshNetSftpSessionFactory();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => factory.ConnectAsync(Settings(value), "sftp-server-1", EtlContext()));
        Assert.Contains("sftp-server-1", ex.Message);
    }
}

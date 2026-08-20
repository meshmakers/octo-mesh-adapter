using System.Collections.Concurrent;
using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

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
            () => factory.ConnectAsync(Settings(3), "sftp-server-1", etlContext));

        Assert.True(properties.ContainsKey("SftpSessionFactory.Semaphores"));
        var semaphores = Assert.IsType<ConcurrentDictionary<string, SemaphoreSlim>>(
            properties["SftpSessionFactory.Semaphores"]);
        Assert.True(semaphores.ContainsKey("sftp-server-1"));
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

        await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(settings, "sftp-server-1", EtlContext()));

        // With a leaked slot the second attempt would wait forever instead of failing.
        var second = factory.ConnectAsync(settings, "sftp-server-1", EtlContext());
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

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConnectAsync_NonPositiveMaxConcurrentConnections_ThrowsBeforeConnecting(int value)
    {
        var factory = new SshNetSftpSessionFactory();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => factory.ConnectAsync(Settings(value), "sftp-server-1"));
        Assert.Contains("sftp-server-1", ex.Message);
    }
}

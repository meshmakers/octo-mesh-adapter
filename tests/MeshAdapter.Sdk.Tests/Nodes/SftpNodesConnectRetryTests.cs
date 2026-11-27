using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// <c>connectRetry</c> on each of the four SFTP nodes: the node hands its option to the session
/// factory (a node that ignored it would connect once whatever its definition says), a failure
/// after every attempt takes the same path as a single failed connection, and the node connects
/// once when an operation on the open session fails.
/// </summary>
public class SftpNodesConnectRetryTests : NodeTestBase
{
    private const string RemoteFile = "/out/AR00001.TXT";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();

    public SftpNodesConnectRetryTests()
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => _etlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => globalConfiguration.IsDefined("s")).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<SftpServerSettings>("s"))
            .Returns(new SftpServerSettings { Host = "sftp.invalid", Username = "user", Password = "secret" });
    }

    public static TheoryData<string> Nodes => ["SftpList@1", "SftpDownload@1", "SftpUpload@1", "SftpDelete@1"];

    /// <summary>The wrapper each node puts around a failed connection, the same as before the option.</summary>
    private static string WrapperOf(string node) => node switch
    {
        "SftpList@1" => "Cannot list directory via SFTP: ",
        "SftpDownload@1" => "Cannot download file via SFTP: ",
        "SftpUpload@1" => "Cannot upload file via SFTP: ",
        _ => $"Cannot delete file '{RemoteFile}' via SFTP: "
    };

    /// <summary>Runs the node once and returns its failure, if any, with the node context it ran in.</summary>
    private async Task<(Exception? Failure, INodeContext NodeContext)> RunAsync(string node,
        SftpConnectRetryOptions? retry, ISftpSessionFactory factory)
    {
        INodeConfiguration config = node switch
        {
            "SftpList@1" => new SftpListNodeConfiguration
            {
                ServerConfiguration = "s", RemoteDirectory = "/out", FilePattern = "*", TargetPath = "$.files",
                ConnectRetry = retry
            },
            "SftpDownload@1" => new SftpDownloadNodeConfiguration
            {
                ServerConfiguration = "s", RemotePath = RemoteFile, TargetPath = "$.content", ConnectRetry = retry
            },
            "SftpUpload@1" => new SftpUploadNodeConfiguration
            {
                ServerConfiguration = "s", RemoteDirectory = "/in", FileName = "a.txt", Path = "$.content",
                ConnectRetry = retry
            },
            _ => new SftpDeleteNodeConfiguration { ServerConfiguration = "s", RemotePath = RemoteFile, ConnectRetry = retry }
        };
        var (data, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(data, "$.content", "hello");
        IPipelineNode instance = node switch
        {
            "SftpList@1" => new SftpListNode(next, _etlContext, factory),
            "SftpDownload@1" => new SftpDownloadNode(next, _etlContext, factory),
            "SftpUpload@1" => new SftpUploadNode(next, _etlContext, factory),
            _ => new SftpDeleteNode(next, _etlContext, factory)
        };

        try
        {
            await instance.ProcessObjectAsync(data, nodeContext);
            return (null, nodeContext);
        }
        catch (Exception e)
        {
            return (e, nodeContext);
        }
    }

    /// <summary>A faked factory that records the settings it was asked to connect with.</summary>
    private static ISftpSessionFactory RecordingFactory(List<SftpServerSettings> received, ISftpSession session)
    {
        var factory = A.Fake<ISftpSessionFactory>();
        A.CallTo(() => factory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
                A<INodeContext>._, A<CancellationToken>._))
            .ReturnsLazily((SftpServerSettings s, string _, IMeshEtlContext _, INodeContext _, CancellationToken _) =>
            {
                received.Add(s);
                return Task.FromResult(session);
            });
        return factory;
    }

    [Theory]
    [MemberData(nameof(Nodes))]
    public async Task Node_HandsItsOptionToTheSessionFactory_AndNoneWithoutIt(string node)
    {
        var session = A.Fake<ISftpSession>();
        A.CallTo(() => session.List(A<string>._)).Returns(new List<SftpEntry>());
        A.CallTo(() => session.Download(A<string>._, A<long>._)).Returns([]);
        A.CallTo(() => session.Delete(A<string>._)).Returns(true);
        var received = new List<SftpServerSettings>();

        var (withFailure, _) = await RunAsync(node, new SftpConnectRetryOptions { MaxAttempts = 4, BackoffBaseSeconds = 15 },
            RecordingFactory(received, session));
        var (withoutFailure, _) = await RunAsync(node, null, RecordingFactory(received, session));

        Assert.Null(withFailure);
        Assert.Null(withoutFailure);
        Assert.Equal(2, received.Count);
        Assert.Equal(4, received[0].ConnectRetry!.MaxAttempts);
        Assert.Equal(15, received[0].ConnectRetry!.BackoffBaseSeconds);
        Assert.Null(received[1].ConnectRetry);
    }

    [Theory]
    [MemberData(nameof(Nodes))]
    public async Task Node_EveryAttemptRefused_FailsThroughTheSamePathAsASingleRefusal(string node)
    {
        // Through the real session factory, once with a single attempt and once with three: both
        // fail with the node's own wrapper around the connection failure; the one that retried
        // names its attempts and keeps the last failure underneath.
        var calls = 0;
        var factory = new SshNetSftpSessionFactory(new FakeTimeProvider(), (_, _) =>
        {
            calls++;
            throw new SshConnectionException("Connection refused by the test server.");
        });

        var (single, singleContext) = await RunAsync(node, null, factory);
        Assert.Equal(1, calls);
        var (retried, retriedContext) = await RunAsync(node,
            new SftpConnectRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 }, factory);
        Assert.Equal(4, calls);

        var singleException = Assert.IsType<MeshAdapterPipelineExecutionException>(single);
        Assert.Equal($"[{singleContext.NodePath}]: {WrapperOf(node)}Connection refused by the test server.",
            singleException.Message);
        Assert.IsType<SshConnectionException>(singleException.InnerException);

        var retriedException = Assert.IsType<MeshAdapterPipelineExecutionException>(retried);
        Assert.Equal($"[{retriedContext.NodePath}]: {WrapperOf(node)}Connecting failed in all 3 attempts the node's connectRetry allows; the last attempt failed with SshConnectionException: Connection refused by the test server.",
            retriedException.Message);
        var connectFailed = Assert.IsType<SftpConnectFailedException>(retriedException.InnerException);
        Assert.IsType<SshConnectionException>(connectFailed.InnerException);
    }

    [Theory]
    [MemberData(nameof(Nodes))]
    public async Task Node_OperationOnTheOpenSessionFails_TheNodeConnectsOnce(string node)
    {
        // Faked factory; the real one stops after its first successful connect (SshNetSftpSessionFactoryRetryTests, row "nothing refused").
        var session = A.Fake<ISftpSession>();
        var broken = new InvalidOperationException("Connection lost.");
        A.CallTo(() => session.List(A<string>._)).Throws(broken);
        A.CallTo(() => session.Download(A<string>._, A<long>._)).Throws(broken);
        A.CallTo(() => session.EnsureDirectory(A<string>._)).Throws(broken);
        A.CallTo(() => session.Delete(A<string>._)).Throws(broken);
        var received = new List<SftpServerSettings>();

        var (failure, _) = await RunAsync(node, new SftpConnectRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 },
            RecordingFactory(received, session));

        Assert.Contains(WrapperOf(node) + "Connection lost.", Assert.IsType<MeshAdapterPipelineExecutionException>(failure).Message);
        Assert.Single(received);
        A.CallTo(() => session.Dispose()).MustHaveHappenedOnceExactly();
    }
}

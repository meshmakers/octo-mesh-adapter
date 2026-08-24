using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpDownloadNodeTests : NodeTestBase
{
    private const string ServerConfig = "LkvSftp";
    private const string TargetPath = "$.fileContent";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpDownloadNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task ProcessObjectAsync_StaticPathWithLatin1_WritesDecodedContent()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            Encoding = "iso-8859-1",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/AR00001.TXT", A<long>._))
            .Returns(new byte[] { 0x47, 0x72, 0xFC, 0x73, 0x73, 0x65 });

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(TargetPath, "Grüsse", A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PathFromDataContext_TakesPrecedence()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/static.TXT",
            RemotePathPath = "$.key.fullPath",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/dynamic.TXT", A<long>._)).Returns("ok"u8.ToArray());

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.key.fullPath")).Returns("/dynamic.TXT");

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Download("/dynamic.TXT", A<long>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Download("/static.TXT", A<long>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoPathConfigured_Throws()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            TargetPath = TargetPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("remotePath", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_PathResolvesToNothing_Throws()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = "$.key.fullPath",
            TargetPath = TargetPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.key.fullPath")).Returns(null);

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        await Assert.ThrowsAsync<PipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_DownloadFails_ReportsWithNodeContext()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/missing.TXT",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/missing.TXT", A<long>._)).Throws(new InvalidOperationException("no such file"));

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("no such file", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_PassesTheConfiguredSizeCapToTheSession()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            MaxFileSizeBytes = 4096,
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/AR00001.TXT", 4096)).Returns("ok"u8.ToArray());

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The cap has to reach the session: the node never sees the bytes until the read is
        // over, so enforcing it here would mean the pod had already paid for them.
        A.CallTo(() => _session.Download("/AR00001.TXT", 4096)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void MaxFileSizeBytes_DefaultsToOneHundredMebibytes()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            TargetPath = TargetPath
        };

        Assert.Equal(100L * 1024 * 1024, config.MaxFileSizeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProcessObjectAsync_NonPositiveMaxFileSizeBytes_ThrowsBeforeConnecting(long value)
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            MaxFileSizeBytes = value,
            TargetPath = TargetPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        // Zero is not a synonym for "no limit": the content becomes a string, so there is no
        // size this node could usefully read without a bound.
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("MaxFileSizeBytes", ex.Message);
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_FileExceedsTheCap_NamesTheNodeAndTheLimit()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/huge.bin",
            MaxFileSizeBytes = 1024,
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/huge.bin", 1024))
            .Throws(new SftpFileTooLargeException("/huge.bin", 5_368_709_120, 1024));

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("/huge.bin", ex.Message);
        Assert.Contains("MaxFileSizeBytes", ex.Message);
        // Nothing was written, so the downstream chain must not run on a half-read file.
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Encoding_UnknownName_IsRejectedWhenBound()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            TargetPath = TargetPath
        };

        // A typo fails the deployment rather than the first download.
        Assert.Throws<ArgumentException>(() => config.Encoding = "not-an-encoding");
    }

    [Fact]
    public void Encoding_DefaultsToUtf8()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            TargetPath = TargetPath
        };

        Assert.Equal("utf-8", config.Encoding);
    }
}

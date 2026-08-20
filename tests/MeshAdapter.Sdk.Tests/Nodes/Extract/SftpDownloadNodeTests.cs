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
            A<CancellationToken>._))
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
        A.CallTo(() => _session.Download("/AR00001.TXT"))
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
            RemotePathPath = "$.current.fullPath",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/dynamic.TXT")).Returns("ok"u8.ToArray());

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.current.fullPath")).Returns("/dynamic.TXT");

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Download("/dynamic.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Download("/static.TXT")).MustNotHaveHappened();
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
            RemotePathPath = "$.current.fullPath",
            TargetPath = TargetPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.current.fullPath")).Returns(null);

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        await Assert.ThrowsAsync<PipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
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

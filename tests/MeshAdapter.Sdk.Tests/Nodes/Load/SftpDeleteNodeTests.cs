using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SftpDeleteNodeTests : NodeTestBase
{
    private const string ServerConfig = "LkvSftp";

    // Deliberately not called RemotePath: that is the name of the configuration property, and
    // "RemotePath = RemotePath" in an initializer is legal but reads like a mistake.
    private const string RemoteFile = "/out/AR00001.TXT";
    private const string DynamicPath = "$.current.fullPath";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpDeleteNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).Returns(Task.FromResult(_session));
        A.CallTo(() => _session.Delete(A<string>._)).Returns(true);
    }

    private SftpDeleteNode CreateNode(NodeDelegate next)
    {
        return new SftpDeleteNode(next, _etlContext, _sessionFactory);
    }

    [Fact]
    public async Task ProcessObjectAsync_StaticPath_DeletesItAndReleasesTheSession()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);
        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Delete(RemoteFile)).MustHaveHappenedOnceExactly();
        // The session holds the server's concurrency slot; releasing it twice or not at all
        // both move that limit for the rest of the process.
        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PathFromDataContext_TakesPrecedence()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/out/static.TXT",
            RemotePathPath = DynamicPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, DynamicPath, "/out/dynamic.TXT");
        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Delete("/out/dynamic.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Delete("/out/static.TXT")).MustNotHaveHappened();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoPathConfigured_ThrowsAndOpensNoSession()
    {
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = ServerConfig };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("No remote path specified", ex.Message);
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessObjectAsync_DynamicPathResolvesToBlank_ThrowsValueNotSet(string? resolved)
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = DynamicPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);
        // Stated rather than inherited: what an unconfigured fake returns for a string is not
        // something this test should depend on.
        SetupGetSimpleValueByPath(dataContext, DynamicPath, resolved);

        var ex = await Assert.ThrowsAnyAsync<PipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        // Message text read out of Sdk.Pipeline 3.4.108: "Value not set. Value path: '<path>'".
        Assert.Contains(DynamicPath, ex.Message);
        A.CallTo(() => _session.Delete(A<string>._)).MustNotHaveHappened();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PathNamesADirectory_IsRefusedBeforeConnecting()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/out/"
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("names a directory", ex.Message);
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_SessionFails_IsReportedAsADeleteFailureNamingTheNode()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };
        A.CallTo(() => _session.Delete(RemoteFile)).Throws(new InvalidOperationException("permission denied"));

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Cannot delete file via SFTP", ex.Message);
        Assert.Contains("permission denied", ex.Message);
        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_FactoryFailure_PassesThroughUnwrapped()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        // Built with the node context under test, not a stray fake: the factory's message
        // names the node, and that is exactly what must survive.
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
                A<INodeContext>._, A<CancellationToken>._))
            .Throws(MeshAdapterPipelineExecutionException.SftpSlotWaitTimedOut(nodeContext, ServerConfig, 30));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("no free connection slot", ex.Message);
        Assert.DoesNotContain("Cannot delete file via SFTP", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_ServerConfigurationUnknown_Throws()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = "does-not-exist",
            RemotePath = RemoteFile
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.DoesNotContain("Cannot delete file via SFTP", ex.Message);
        A.CallTo(() => _session.Delete(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_DownstreamFails_DoesNotBlameTheDeletion()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._))
            .Throws(new InvalidOperationException("the node after this one failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Equal("the node after this one failed", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_FileAlreadyGoneAndIgnore_WarnsAndContinues()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            MissingFileHandling = MissingFileHandling.Ignore
        };
        A.CallTo(() => _session.Delete(RemoteFile)).Returns(false);

        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger<SftpDeleteNodeConfiguration>(config);
        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // IPipelineLogger.Warning(nodeId, nodePath, message, args); the node passes the format
        // string and the path as an argument, the way SftpListNode.cs:134 does.
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
            A<string>.That.Contains("does not exist any more"), A<object[]>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_FileAlreadyGoneAndFail_ThrowsNamingThePath()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };
        A.CallTo(() => _session.Delete(RemoteFile)).Returns(false);

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains(RemoteFile, ex.Message);
        // The file was gone, not unreachable: reporting this as a transport failure would send
        // an operator looking for a broken connection.
        Assert.DoesNotContain("Cannot delete file via SFTP", ex.Message);
        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_DryRun_DeletesNothingAndOpensNoSession()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpDeleteNodeConfiguration>(config,
            executionMode: new DefaultPipelineExecutionMode { IsDryRun = true });
        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _session.Delete(A<string>._)).MustNotHaveHappened();
        VerifyNextCalled(next, dataContext, nodeContext);
    }
}

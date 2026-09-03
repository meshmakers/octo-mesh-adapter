using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SftpUploadNodeTests : NodeTestBase
{
    private const string TestServerConfig = "sftp-server-1";
    private const string TestRemoteDir = "/upload/test";
    private const string TestFileName = "report.csv";
    private const string TestFileNamePath = "$.fileName";
    private const string TestFileRtId = "000000000000000000000099";
    private const string TestFileRtIdPath = "$.fileRtId";
    private const string TestContentPath = "$.content";

    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;
    private readonly Dictionary<string, object?> _properties;
    private readonly ISftpSessionFactory _sftpSessionFactory;
    private readonly ISftpSession _sftpSession;

    public SftpUploadNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();
        _properties = new Dictionary<string, object?>();

        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.Properties).Returns(_properties);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));

        _sftpSessionFactory = A.Fake<ISftpSessionFactory>();
        _sftpSession = A.Fake<ISftpSession>();
        A.CallTo(() => _sftpSessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).Returns(Task.FromResult(_sftpSession));
    }

    private SftpUploadNode CreateNode(NodeDelegate next)
    {
        return new SftpUploadNode(next, _etlContext, _sftpSessionFactory);
    }

    #region Configuration Validation Tests

    [Fact]
    public async Task ProcessObjectAsync_NoFileNameConfigured_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            Path = TestContentPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("File name is not configured", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoFileSourceConfigured_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = null!
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("No file source specified", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_BothBinaryAndStringSourceConfigured_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtId = TestFileRtId,
            Path = TestContentPath
        };

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Multiple file sources specified", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_GlobalConfigNotFound_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtId = TestFileRtId,
            Path = null!
        };

        A.CallTo(() => _globalConfiguration.IsDefined(TestServerConfig)).Returns(false);

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Global configuration parameter", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoAuthConfigured_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtId = TestFileRtId,
            Path = null!
        };

        SetupGlobalConfig(password: null, privateKey: null);

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("No SFTP authentication configured", ex.Message);
    }

    #endregion

    #region File Name Resolution Tests

    [Fact]
    public async Task ProcessObjectAsync_FileNamePathResolvesToNull_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileNamePath = TestFileNamePath,
            FileRtId = TestFileRtId,
            Path = null!
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath<string?>(dataContext, TestFileNamePath, null);

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("File name is null", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_FileNamePathTakesPrecedenceOverFileName_UsesFileNamePath()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = "static.csv",
            FileNamePath = TestFileNamePath,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestFileNamePath, "dynamic.csv");
        SetupGetSimpleValueByPath<string?>(dataContext, TestContentPath, null);

        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        A.CallTo(() => dataContext.Get<string>(TestFileNamePath))
            .MustHaveHappenedOnceExactly();
    }

    #endregion

    #region Path Traversal Prevention Tests

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret.txt")]
    [InlineData("/absolute/path/file.txt")]
    [InlineData("sub/dir/file.txt")]
    public async Task ProcessObjectAsync_FileNameWithPathComponents_SanitizesToBaseName(string maliciousFileName)
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileNamePath = TestFileNamePath,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestFileNamePath, maliciousFileName);
        SetupGetSimpleValueByPath<string?>(dataContext, TestContentPath, null);

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.DoesNotContain("Invalid file name", ex.Message);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task ProcessObjectAsync_FileNameIsTraversalOnly_ThrowsInvalidFileName(string traversalName)
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileNamePath = TestFileNamePath,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestFileNamePath, traversalName);

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Invalid file name", ex.Message);
    }

    #endregion

    #region Binary Source Tests

    [Fact]
    public async Task ProcessObjectAsync_FileRtIdResolvesToNull_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtIdPath = TestFileRtIdPath,
            Path = null!
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath<string?>(dataContext, TestFileRtIdPath, null);

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Value of RtId is null", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_BinaryNotFoundInStorage_ThrowsBinaryNotFoundException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtId = TestFileRtId,
            Path = null!
        };

        SetupGlobalConfig();

        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(
                _session, A<OctoObjectId>._, A<CancellationToken>._))
            .Returns(Task.FromResult((IDownloadStreamHandler)null!));

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Binary file with RtId", ex.Message);
        Assert.Contains("not found in storage", ex.Message);
    }

    #endregion

    #region String Content Source Tests

    [Fact]
    public async Task ProcessObjectAsync_StringContentIsNull_ThrowsException()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath<string?>(dataContext, TestContentPath, null);

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains(TestContentPath, ex.Message);
    }

    [Fact]
    public async Task GetUploadStreamAsync_BinarySource_IgnoresEncodingConfiguration()
    {
        // Ticket criterion "binary sources are unaffected": bytes must pass through 1:1,
        // even with a restrictive encoding and Fail mode configured.
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            FileRtId = TestFileRtId,
            Path = null!,
            Encoding = "windows-1252",
            OnEncodingError = EncodingErrorHandling.Fail
        };

        // UTF-8 bytes of "€" + a musical symbol — as STRING content this would be
        // unencodable in windows-1252; as binary payload it must survive untouched.
        var payload = new byte[] { 0xE2, 0x82, 0xAC, 0xF0, 0x9D, 0x84, 0x9E };
        var handler = A.Fake<IDownloadStreamHandler>();
        A.CallTo(() => handler.Stream).Returns(new MemoryStream(payload));
        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(
                _session, A<OctoObjectId>._, A<CancellationToken>._))
            .Returns(Task.FromResult(handler));

        var (dataContext, nodeContext, _) = PrepareTest<SftpUploadNodeConfiguration>(config);
        var node = CreateNode(A.Fake<NodeDelegate>());

        await using var stream = await node.GetUploadStreamAsync(config, dataContext, nodeContext);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task ProcessObjectAsync_EncodingFailModeWithUnencodableContent_ThrowsBeforeUpload()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath,
            Encoding = "windows-1252",
            OnEncodingError = EncodingErrorHandling.Fail
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestContentPath, "a\U0001D11Eb");

        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("U+1D11E", ex.Message);
        Assert.Contains("no file was written", ex.Message);
        Assert.DoesNotContain("Cannot upload file via SFTP", ex.Message);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    #endregion

    #region Session Handling Tests

    [Fact]
    public async Task ProcessObjectAsync_ConnectsThroughTheSessionFactoryAndDisposesTheSession()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestContentPath, "content");

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // The concurrency slot is held by the session, so it is only handed back on dispose.
        A.CallTo(() => _sftpSessionFactory.ConnectAsync(A<SftpServerSettings>.That.Matches(
                s => s.Host == "localhost" && s.Username == "testuser"),
            TestServerConfig, A<IMeshEtlContext>._, A<INodeContext>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _sftpSession.Dispose()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_DryRunAndDownstreamFails_DoesNotBlameTheUpload()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config,
            executionMode: new DefaultPipelineExecutionMode { IsDryRun = true });
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._))
            .Throws(new InvalidOperationException("the node after this one failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        // The catch in this node speaks for the upload. Running the rest of the chain inside
        // it turned every downstream failure into "Cannot upload file via SFTP", pointing an
        // operator at the one step that had done nothing at all.
        Assert.Equal("the node after this one failed", ex.Message);
        A.CallTo(() => _sftpSessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_DownstreamFails_DoesNotBlameTheUpload()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestContentPath, "content");
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._))
            .Throws(new InvalidOperationException("the node after this one failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("the node after this one failed", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_StringContent_UploadsEncodedBytesToResolvedPath()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath,
            Encoding = "iso-8859-1"
        };

        SetupGlobalConfig();

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, TestContentPath, "Grüsse");

        byte[]? uploaded = null;
        A.CallTo(() => _sftpSession.Upload(A<Stream>._, A<string>._))
            .Invokes((Stream content, string _) =>
            {
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                uploaded = buffer.ToArray();
            });

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Creating the directory after writing into it would be useless, so the order is part
        // of the contract rather than an accident of the implementation.
        A.CallTo(() => _sftpSession.EnsureDirectory(TestRemoteDir)).MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => _sftpSession.Upload(A<Stream>._, TestRemoteDir + "/" + TestFileName))
                .MustHaveHappenedOnceExactly());
        Assert.NotNull(uploaded);
        // In ISO-8859-1 the umlaut is a single byte; in UTF-8 it would be two.
        Assert.Equal(6, uploaded!.Length);
        Assert.Equal(0xFC, uploaded[2]);
    }

    #endregion

    #region Helpers

    private void SetupGlobalConfig(string? password = "testpass", string? privateKey = null)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(TestServerConfig)).Returns(true);

        var serverConfigJson = $$"""
        {
            "Host": "localhost",
            "Port": 22,
            "Username": "testuser",
            "Password": {{(password is null ? "null" : "\"" + password + "\"")}},
            "PrivateKey": {{(privateKey is null ? "null" : "\"" + privateKey + "\"")}},
            "MaxConcurrentConnections": 3
        }
        """;

        // Deserialize into whatever type the caller asks for, so this helper keeps working
        // whichever settings type the node resolves.
        A.CallTo(_globalConfiguration)
            .Where(call => call.Method.Name == "GetValue" && call.Method.IsGenericMethod)
            .WithNonVoidReturnType()
            .ReturnsLazily(call =>
            {
                var type = call.Method.GetGenericArguments()[0];
                return JsonSerializer.Deserialize(serverConfigJson, type)!;
            });
    }

    #endregion
}

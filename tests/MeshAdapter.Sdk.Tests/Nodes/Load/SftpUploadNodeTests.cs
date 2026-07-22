using System.Collections.Concurrent;
using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

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
    }

    private SftpUploadNode CreateNode(NodeDelegate next)
    {
        return new SftpUploadNode(next, _etlContext);
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

    #region Semaphore Thread-Safety Tests

    [Fact]
    public async Task ProcessObjectAsync_StoresSemaphoreDictionaryInProperties()
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

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.True(_properties.ContainsKey("SftpUploadNode.Semaphores"));
        Assert.IsType<ConcurrentDictionary<string, SemaphoreSlim>>(_properties["SftpUploadNode.Semaphores"]);
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

        // SftpServerConfiguration is private; deserialize dynamically using STJ.
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

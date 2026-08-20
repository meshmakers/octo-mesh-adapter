using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Sftp;

namespace MeshAdapter.Sdk.Tests.Nodes.Sftp;

public class SftpServerSettingsResolverTests : NodeTestBase
{
    private const string ServerConfig = "sftp-server-1";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    public SftpServerSettingsResolverTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private INodeContext NodeContext()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = "/out",
            FileName = "x.txt",
            Path = "$.content"
        };
        var (_, nodeContext, _) = PrepareTest<SftpUploadNodeConfiguration>(config);
        return nodeContext;
    }

    [Fact]
    public void Resolve_EntryNotDefined_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(false);

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains(ServerConfig, ex.Message);
    }

    [Fact]
    public void Resolve_NeitherPasswordNorPrivateKey_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user" });

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_PasswordConfigured_ReturnsSettingsWithDefaults()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });

        var settings = SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext());

        Assert.Equal("sftp.example.com", settings.Host);
        Assert.Equal(22, settings.Port);
        Assert.Equal(3, settings.MaxConcurrentConnections);
        Assert.Null(settings.HostKeyFingerprint);
    }

    [Fact]
    public void Resolve_PrivateKeyOnly_IsAccepted()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----"
            });

        var settings = SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext());

        Assert.Null(settings.Password);
        Assert.NotNull(settings.PrivateKey);
    }
}

using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_NonPositiveMaxConcurrentConnections_Throws(int value)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                Password = "secret",
                MaxConcurrentConnections = value
            });

        // Caught while resolving, so a misconfigured entry fails before the node does any
        // work - the upload node otherwise downloads a binary from storage first.
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("MaxConcurrentConnections", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_PresentButBlankHostKeyFingerprint_Throws(string fingerprint)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                Password = "secret",
                HostKeyFingerprint = fingerprint
            });

        // Leaving the field out disables pinning on purpose. A blank value is a typo, and
        // silently accepting it leaves an operator believing the server is pinned.
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("HostKeyFingerprint", ex.Message);
    }

    [Fact]
    public void Settings_ToString_DoesNotRevealSecrets()
    {
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "super-secret",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----",
            PrivateKeyPassphrase = "phrase"
        };

        var text = settings.ToString();

        // One interpolation into a log line would otherwise ship the credentials to Loki.
        Assert.DoesNotContain("super-secret", text);
        Assert.DoesNotContain("BEGIN OPENSSH", text);
        Assert.DoesNotContain("phrase", text);
        Assert.Contains("sftp.example.com", text);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void Resolve_NegativeTimeout_Throws(int connect, int operation, int wait)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                Password = "secret",
                ConnectTimeoutSeconds = connect,
                OperationTimeoutSeconds = operation,
                WaitForSlotTimeoutSeconds = wait
            });

        // Zero means "leave it as it is". A negative value is a mistake, and silently reading
        // it as zero would leave an operator believing a limit is in place.
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("Seconds", ex.Message);
    }

    [Theory]
    [InlineData(2147484, 0, 0)]
    [InlineData(0, 2147484, 0)]
    [InlineData(0, 0, 2147484)]
    public void Resolve_TimeoutBeyondWhatTheTimersAccept_Throws(int connect, int operation, int wait)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                Password = "secret",
                ConnectTimeoutSeconds = connect,
                OperationTimeoutSeconds = operation,
                WaitForSlotTimeoutSeconds = wait
            });

        // One second past what an Int32 millisecond count holds. Left to the timers, it comes
        // back as a bare ArgumentOutOfRangeException that names neither the property nor the
        // configuration entry - and the connect timeout only reaches its setter once the
        // client has been created.
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("Seconds", ex.Message);
        Assert.Contains(ServerConfig, ex.Message);
    }

    [Fact]
    public void Resolve_TimeoutAtTheCeiling_IsAccepted()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings
            {
                Host = "sftp.example.com",
                Username = "user",
                Password = "secret",
                ConnectTimeoutSeconds = 2147483,
                OperationTimeoutSeconds = 2147483,
                WaitForSlotTimeoutSeconds = 2147483
            });

        var settings = SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext());

        Assert.Equal(2147483, settings.ConnectTimeoutSeconds);
    }

    [Fact]
    public void Resolve_PayloadThatDoesNotFitTheShape_NamesTheNodeAndTheEntry()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Throws(new JsonException("The JSON value could not be converted to System.Int32. Path: $.port"));

        var nodeContext = NodeContext();

        // Left alone, the deserializer's message reaches the run as a bare sentence about a
        // JSON path, with nothing saying which node or which configuration entry it belongs to.
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, nodeContext));
        Assert.Contains(ServerConfig, ex.Message);
        Assert.Contains("System.Int32", ex.Message);
        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
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
    public void Resolve_PayloadWithNullMaxConcurrentConnections_PassesTheNonPositiveGuard()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns("""
                     {
                       "host": "sftp.example.com",
                       "username": "user",
                       "password": "secret",
                       "maxConcurrentConnections": null
                     }
                     """.Deserialize<SftpServerSettings>());

        var settings = SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext());

        // Deserializing the payload and validating it are two different steps, and the second
        // one only helps if the first produced the default: reading the unset attribute as zero
        // would trip the non-positive guard instead of connecting.
        Assert.Equal(3, settings.MaxConcurrentConnections);
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

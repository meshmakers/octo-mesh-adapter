using System.Globalization;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpListNodeTests : NodeTestBase
{
    private const string ServerConfig = "LkvSftp";
    private const string RemoteDir = "/";
    private const string TargetPath = "$.files";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpListNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(_session));
    }

    private static SftpListNodeConfiguration Config(string filePattern = "AR*TXT", int minAgeSeconds = 0)
    {
        return new SftpListNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = RemoteDir,
            FilePattern = filePattern,
            MinFileAgeSeconds = minAgeSeconds,
            TargetPath = TargetPath
        };
    }

    private static SftpEntry File(string name, DateTime? lastWrite = null, long length = 430)
    {
        return new SftpEntry(name, "/" + name, false, length, lastWrite ?? DateTime.UtcNow.AddHours(-1));
    }

    private async Task<JsonArray?> RunAsync(SftpListNodeConfiguration config)
    {
        var (dataContext, nodeContext, next) = PrepareTest<SftpListNodeConfiguration>(config);

        JsonArray? emitted = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonArray>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonArray value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                emitted = value);

        var node = new SftpListNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        return emitted;
    }

    [Fact]
    public async Task ProcessObjectAsync_FiltersByPatternAndSortsOrdinal()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>
        {
            File("ARa.TXT"),
            File("BE00001.txt"),
            File("AR_1.TXT"),
            // Matches the pattern too, so only the directory check can keep it out.
            new("AR_archive.TXT", "/AR_archive.TXT", true, 0, DateTime.UtcNow.AddHours(-1))
        });

        var emitted = await RunAsync(Config());

        Assert.NotNull(emitted);
        Assert.Equal(2, emitted!.Count);
        // '_' (0x5F) sorts before 'a' (0x61) ordinally; a culture-aware comparer puts the
        // punctuation elsewhere, so this pair is what pins StringComparer.Ordinal.
        Assert.Equal("AR_1.TXT", emitted[0]!["name"]!.GetValue<string>());
        Assert.Equal("ARa.TXT", emitted[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_YoungerThanMinFileAge_IsOmitted()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>
        {
            File("AR00001.TXT", DateTime.UtcNow.AddSeconds(-5))
        });

        var emitted = await RunAsync(Config(minAgeSeconds: 60));

        Assert.NotNull(emitted);
        Assert.Empty(emitted!);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoMatches_StillWritesAnEmptyArray()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>());

        var emitted = await RunAsync(Config());

        // A downstream ForEach@1 aborts with PathMustBeArray when the path holds no array.
        Assert.NotNull(emitted);
        Assert.Empty(emitted!);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmitsPathLengthAndSourceOnEveryElement()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT", length: 512) });

        var emitted = await RunAsync(Config());

        var element = emitted![0]!;
        Assert.Equal("/AR00001.TXT", element["fullPath"]!.GetValue<string>());
        Assert.Equal(512, element["length"]!.GetValue<long>());

        var source = element["source"]!;
        Assert.Equal(ServerConfig, source["serverConfiguration"]!.GetValue<string>());
        Assert.Equal(RemoteDir, source["remoteDirectory"]!.GetValue<string>());
        Assert.Equal("AR*TXT", source["filePattern"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_EmitsRoundTripStableTimestamp()
    {
        var lastWrite = new DateTime(2026, 8, 20, 11, 27, 35, DateTimeKind.Utc).AddTicks(8850000);
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT", lastWrite) });

        var first = await RunAsync(Config());
        var second = await RunAsync(Config());

        var firstStamp = first![0]!["lastWriteTimeUtc"]!.GetValue<string>();
        var secondStamp = second![0]!["lastWriteTimeUtc"]!.GetValue<string>();

        // A consumer builds a file identity from this string. Were it unstable, the identity
        // would change on every listing and no file would ever count as already processed.
        Assert.Equal(firstStamp, secondStamp);
        Assert.Equal(lastWrite,
            DateTime.Parse(firstStamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task ProcessObjectAsync_EmitsTheSameInstantRegardlessOfKind(DateTimeKind kind)
    {
        var lastWrite = DateTime.SpecifyKind(
            new DateTime(2026, 8, 20, 11, 27, 35, DateTimeKind.Unspecified).AddTicks(8850000), kind);
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT", lastWrite) });

        var emitted = await RunAsync(Config());

        // The round-trip specifier renders a Local value with a daylight-saving-dependent
        // offset and an Unspecified one with no zone at all. A consumer builds a file identity
        // from this string, so the same instant has to render identically either way, or the
        // identity silently changes and nothing counts as processed any more.
        Assert.Equal("2026-08-20T11:27:35.8850000Z", emitted![0]!["lastWriteTimeUtc"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_ClosesTheSessionBeforeContinuing()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT") });

        await RunAsync(Config());

        A.CallTo(() => _session.Dispose()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyFilePattern_Throws()
    {
        var config = Config(filePattern: "");
        var (dataContext, nodeContext, next) = PrepareTest<SftpListNodeConfiguration>(config);
        var node = new SftpListNode(next, _etlContext, _sessionFactory);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("filePattern", ex.Message);
    }
}

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

/// <summary>
/// <c>SftpList@1</c> and the temporary name of <c>SftpDelete@1</c> (AB#8284): a file under that name
/// was already handed to a delete, so a listing offers it only to a pattern that asks for it, and
/// every other listing of the directory warns about it.
/// </summary>
public class SftpListNodeParkedNameTests : NodeTestBase
{
    private const string ServerConfig = "sftp-server-1";
    private const string RemoteDir = "/out";
    private const string TargetPath = "$.files";
    private const string WarningFormat =
        "SftpList: skipping {0} file(s) in '{1}' under the temporary name SftpDelete@1 moves a file to before deleting it; filePattern '{2}' lists them";

    private static readonly string Parked = $".AR00001.TXT.sftpdelete-{Guid.NewGuid():N}";
    private static readonly string OtherParked = $".BE00002.txt.sftpdelete-{Guid.NewGuid():N}";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpListNodeParkedNameTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
                A<INodeContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(_session));
    }

    private static SftpEntry File(string name)
    {
        return new SftpEntry(name, $"{RemoteDir}/{name}", false, 430, DateTime.UtcNow.AddHours(-1));
    }

    private void Directory(params string[] names)
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(names.Select(File).ToList());
    }

    private async Task<(IReadOnlyList<string> Names, IPipelineLogger Logger)> ListAsync(string filePattern)
    {
        var config = new SftpListNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = RemoteDir,
            FilePattern = filePattern,
            TargetPath = TargetPath
        };
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        JsonArray? emitted = null;
        A.CallTo(() => dataContext.Set(TargetPath, A<JsonArray>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonArray value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                emitted = value);

        await new SftpListNode(next, _etlContext, _sessionFactory).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(emitted);
        return (emitted.Select(e => e!["name"]!.GetValue<string>()).ToList(), logger);
    }

    private static void AssertWarned(IPipelineLogger logger, int count)
    {
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, WarningFormat,
                A<object[]>.That.IsSameSequenceAs(new object[] { count, RemoteDir, ".*.sftpdelete-*" })))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappenedOnceExactly();
    }

    private static void AssertNotWarned(IPipelineLogger logger)
    {
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("*", new[] { "AR00001.TXT", "BE00002.txt" })]
    [InlineData("*.*", new[] { "AR00001.TXT", "BE00002.txt" })]
    [InlineData("?*", new[] { "AR00001.TXT", "BE00002.txt" })]
    [InlineData("*TXT*", new[] { "AR00001.TXT", "BE00002.txt" })]
    [InlineData("*.TXT*", new[] { "AR00001.TXT", "BE00002.txt" })]
    [InlineData("AR*.TXT", new[] { "AR00001.TXT" })]
    public async Task FileUnderTheTemporaryName_IsSkipped_AndWarnedAbout_UnderAPatternWithoutTheMarker(string filePattern,
        string[] expected)
    {
        Directory("AR00001.TXT", Parked, "BE00002.txt");

        var (names, logger) = await ListAsync(filePattern);

        Assert.Equal(expected, names);
        AssertWarned(logger, 1);
    }

    [Theory]
    // match the temporary name in the glob, but do not contain the dotted marker: the exception does
    // not apply, so the file is skipped like under any other pattern
    [InlineData("*sftpdelete*")]
    [InlineData("*?sftpdelete-*")]
    public async Task PatternMatchingTheNameWithoutTheDottedMarker_IsSkipped_AndWarnedAbout(string filePattern)
    {
        Directory("AR00001.TXT", Parked);

        var (names, logger) = await ListAsync(filePattern);

        Assert.Empty(names);
        AssertWarned(logger, 1);
    }

    [Fact]
    public async Task SeveralFilesUnderTheTemporaryName_AreCountedInOneWarning()
    {
        Directory(Parked, "AR00001.TXT", OtherParked);

        var (names, logger) = await ListAsync("*");

        Assert.Equal(["AR00001.TXT"], names);
        AssertWarned(logger, 2);
    }

    [Theory]
    [InlineData(".*.sftpdelete-*")]
    [InlineData(".*.SFTPDELETE-*")]
    [InlineData("*.SftpDelete-*")]
    public async Task PatternNamingTheTemporaryName_ListsTheFiles_WithoutAWarning(string filePattern)
    {
        Directory("AR00001.TXT", Parked, OtherParked);

        var (names, logger) = await ListAsync(filePattern);

        Assert.Equal(new[] { Parked, OtherParked }.Order(StringComparer.Ordinal), names);
        AssertNotWarned(logger);
    }

    [Fact]
    public async Task PatternNamingTheTemporaryName_ListsEverythingItMatches()
    {
        // The exception is about the pattern, not a filter of its own: a pattern that names the
        // marker and also matches other files lists those as before.
        Directory("AR00001.TXT", Parked, "AR.sftpdelete-notes.txt");

        var (names, _) = await ListAsync("*.sftpdelete-*");

        Assert.Equal(new[] { Parked, "AR.sftpdelete-notes.txt" }.Order(StringComparer.Ordinal), names);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("AR*.TXT")]
    public async Task WithoutAFileUnderTheTemporaryName_TheListingIsAsBefore_WithoutAWarning(string filePattern)
    {
        Directory("AR00001.TXT", "AR00002.TXT");

        var (names, logger) = await ListAsync(filePattern);

        Assert.Equal(["AR00001.TXT", "AR00002.TXT"], names);
        AssertNotWarned(logger);
    }

    [Theory]
    // resembles the form, but is not what SftpDelete@1 writes: listed like any other delivery
    [InlineData(".AR00001.TXT.sftpdelete-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcde")]
    [InlineData("AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcdef")]
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcdef.TXT")]
    public async Task NameThatOnlyResemblesTheTemporaryName_IsListed_WithoutAWarning(string name)
    {
        Directory(name);

        var (names, logger) = await ListAsync("*");

        Assert.Equal([name], names);
        AssertNotWarned(logger);
    }

    [Fact]
    public async Task DirectoryWithTheFormOfATemporaryName_IsNeitherListedNorCounted()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>
        {
            new(Parked, $"{RemoteDir}/{Parked}", true, 0, DateTime.UtcNow.AddHours(-1)),
            File("AR00001.TXT")
        });

        var (names, logger) = await ListAsync("*");

        Assert.Equal(["AR00001.TXT"], names);
        AssertNotWarned(logger);
    }
}

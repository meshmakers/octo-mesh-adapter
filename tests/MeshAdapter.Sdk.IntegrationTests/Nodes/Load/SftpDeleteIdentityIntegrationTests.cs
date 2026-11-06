using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
/// Two SFTP servers in containers for the <c>SftpDelete@1</c> identity check (AB#8284): OpenSSH,
/// which refuses a plain rename onto an existing name and offers <c>posix-rename@openssh.com</c>,
/// and SFTPGo, whose rename replaces an existing name. Images are pinned by digest: atmoz/sftp
/// publishes rolling tags only; the SFTPGo digest is the one the tag v2.7.5 carries.
/// </summary>
public sealed class SftpServersFixture : IAsyncLifetime
{
    internal const string User = "tester";
    internal const string Password = "tester-password";

    private IContainer? _openSsh;
    private IContainer? _sftpGo;

    public async ValueTask InitializeAsync()
    {
        // Same .NET 10 regex-timeout workaround the other container fixtures use: the builder
        // parses the image name with a timed regex.
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(10));

        // OpenSSH 8.4p1; the account is chrooted to its home, uploads go to /upload.
        _openSsh = new ContainerBuilder(
                "atmoz/sftp@sha256:0960390462a4441dbb63698d7c185b76a41ffcee7b78ff4adf275f3e66f9c475")
            .WithName($"sftp-openssh-meshadapter-test-{Guid.NewGuid():N}")
            .WithPortBinding(22, true)
            .WithCommand($"{User}:{Password}:::upload")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on"))
            .Build();

        // SFTPGo 2.7.5 in portable mode: one account, the whole directory, every permission.
        _sftpGo = new ContainerBuilder(
                "drakkan/sftpgo@sha256:9011fe608d336d3daf6ed6224b16fd40443aab8f3335e0b20853d6a539c58738")
            .WithName($"sftp-sftpgo-meshadapter-test-{Guid.NewGuid():N}")
            .WithPortBinding(2022, true)
            .WithEntrypoint("sftpgo")
            .WithCommand("portable", "--directory", "/tmp/sftp", "--username", User, "--password", Password,
                "--sftpd-port", "2022", "--permissions", "*")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Portable mode ready"))
            .Build();

        await Task.WhenAll(_openSsh.StartAsync(), _sftpGo.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_openSsh is not null)
        {
            await _openSsh.DisposeAsync();
        }

        if (_sftpGo is not null)
        {
            await _sftpGo.DisposeAsync();
        }
    }

    internal IContainer Container(string server) => server == "openssh" ? _openSsh! : _sftpGo!;

    /// <summary>Where the account writes, as the SFTP session sees it.</summary>
    internal static string Directory(string server) => server == "openssh" ? "/upload" : "/";

    /// <summary>The same directory inside the container, for shell commands.</summary>
    internal static string DirectoryInContainer(string server) =>
        server == "openssh" ? $"/home/{User}/upload" : "/tmp/sftp";

    internal SftpServerSettings Settings(string server)
    {
        var container = Container(server);
        return new SftpServerSettings
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(server == "openssh" ? 22 : 2022),
            Username = User,
            Password = Password,
            MaxConcurrentConnections = 4
        };
    }
}

/// <summary>
/// <c>SftpDelete@1</c> with an expected identity against real SFTP servers, through the real
/// <see cref="SshNetSftpSessionFactory" />: what the session reports and does, and the node from
/// end to end. The unit tests cover every case against an in-memory server; these pin what only a
/// real server and SSH.NET decide.
/// </summary>
public sealed class SftpDeleteIdentityIntegrationTests(SftpServersFixture servers)
    : IClassFixture<SftpServersFixture>, IDisposable
{
    private const string ServerConfig = "sftp";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    public static TheoryData<string> Servers => ["openssh", "sftpgo"];

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>A name of its own per test, so the tests share the servers without meeting.</summary>
    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToUpperInvariant() + ".TXT";

    private static string PathOf(string server, string name) =>
        SftpServersFixture.Directory(server) == "/" ? $"/{name}" : $"{SftpServersFixture.Directory(server)}/{name}";

    private SftpClient Client(string server)
    {
        var settings = servers.Settings(server);
        var client = new SftpClient(settings.Host, settings.Port, SftpServersFixture.User,
            SftpServersFixture.Password);
        client.Connect();
        _disposables.Add(client);
        return client;
    }

    private static void Write(SftpClient client, string path, string content)
    {
        client.UploadFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), path, true);
    }

    private static string Read(SftpClient client, string path)
    {
        using var buffer = new MemoryStream();
        client.DownloadFile(path, buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<string> LeftAside(SftpClient client, string server) =>
        client.ListDirectory(SftpServersFixture.Directory(server))
            .Where(f => f.Name.Contains(".sftpdelete-", StringComparison.Ordinal))
            .Select(f => f.FullName)
            .ToList();

    private (IMeshEtlContext EtlContext, INodeContext Root, DataContextImpl Data) Context(string server)
    {
        var (etlContext, root, data, _) = ContextWithLogger(server);
        return (etlContext, root, data);
    }

    private (IMeshEtlContext EtlContext, INodeContext Root, DataContextImpl Data, IPipelineLogger Logger)
        ContextWithLogger(string server)
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<SftpServerSettings>(ServerConfig)).Returns(servers.Settings(server));

        var data = new DataContextImpl(JsonDocument.Parse("{}"));
        _disposables.Add(data);
        var logger = A.Fake<IPipelineLogger>();
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger, data,
            null, null, null);
        return (etlContext, root, data, logger);
    }

    private async Task<ISftpSession> OpenSessionAsync(string server)
    {
        var (etlContext, root, data) = Context(server);
        var nodeContext = root.RegisterChildNode("SftpDelete", 0,
            new SftpDeleteNodeConfiguration { ServerConfiguration = ServerConfig, RemotePath = "/x" }, data);
        var session = await new SshNetSftpSessionFactory().ConnectAsync(servers.Settings(server), ServerConfig,
            etlContext, nodeContext, TestContext.Current.CancellationToken);
        _disposables.Add(session);
        return session;
    }

    /// <summary>
    /// Lists the file with <c>SftpList@1</c> and hands its element to <c>SftpDelete@1</c> with the
    /// expected identity, as a pipeline does; <paramref name="before" /> runs before a session
    /// operation of the delete node, so a test can change the server between two of its steps.
    /// </summary>
    private async Task<Exception?> ListAndDeleteAsync(string server, string name,
        Action<string, string>? before = null, bool withIdentity = true)
    {
        var (etlContext, root, data) = Context(server);
        var factory = new SshNetSftpSessionFactory();
        await new SftpListNode(A.Fake<NodeDelegate>(), etlContext, factory).ProcessObjectAsync(data,
            root.RegisterChildNode("SftpList", 0, new SftpListNodeConfiguration
            {
                ServerConfiguration = ServerConfig,
                RemoteDirectory = SftpServersFixture.Directory(server),
                FilePattern = name,
                TargetPath = "$.files"
            }, data));
        Assert.Equal(name, data.GetValue("$.files[0].name"));

        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = "$.files[0].fullPath",
            ExpectedLengthPath = withIdentity ? "$.files[0].length" : null,
            ExpectedLastWriteTimeUtcPath = withIdentity ? "$.files[0].lastWriteTimeUtc" : null
        };
        try
        {
            await new SftpDeleteNode(A.Fake<NodeDelegate>(), etlContext, new HookedFactory(factory, before))
                .ProcessObjectAsync(data, root.RegisterChildNode("SftpDelete", 1, config, data));
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    // ---- The session ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task GetEntry_OfAMissingPath_ReturnsNull(string server)
    {
        var session = await OpenSessionAsync(server);

        Assert.Null(session.GetEntry(PathOf(server, NewName("NONE"))));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task GetEntry_ReportsWhatTheListingReports(string server)
    {
        var client = Client(server);
        var path = PathOf(server, NewName("SAME"));
        Write(client, path, "listed content");
        var session = await OpenSessionAsync(server);

        var listed = Assert.Single(session.List(SftpServersFixture.Directory(server)), e => e.FullPath == path);
        var entry = session.GetEntry(path);

        Assert.NotNull(entry);
        Assert.False(entry.IsDirectory);
        Assert.Equal(listed.Length, entry.Length);
        Assert.Equal(listed.LastWriteTimeUtc.Ticks, entry.LastWriteTimeUtc.Ticks);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task GetEntry_ReportsTheLastWriteTime_NotTheLastAccessTime(string server)
    {
        var client = Client(server);
        var path = PathOf(server, NewName("MT"));
        Write(client, path, "listed content");
        // A write time in the past, so it differs from the access time the upload left behind.
        var written = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        client.SetLastWriteTimeUtc(path, written);
        var session = await OpenSessionAsync(server);

        var listed = Assert.Single(session.List(SftpServersFixture.Directory(server)), e => e.FullPath == path);
        var entry = session.GetEntry(path);

        Assert.NotNull(entry);
        Assert.Equal(written.Ticks, listed.LastWriteTimeUtc.Ticks);
        Assert.Equal(written.Ticks, entry.LastWriteTimeUtc.Ticks);
        client.DeleteFile(path);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task GetEntry_OfADirectory_ReportsADirectory(string server)
    {
        var client = Client(server);
        var path = PathOf(server, NewName("DIR"));
        client.CreateDirectory(path);
        var session = await OpenSessionAsync(server);

        var entry = session.GetEntry(path);

        Assert.NotNull(entry);
        Assert.True(entry.IsDirectory);
        client.DeleteDirectory(path);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task MoveToNewName_OfAMissingFile_ReturnsFalse(string server)
    {
        var session = await OpenSessionAsync(server);

        Assert.False(session.MoveToNewName(PathOf(server, NewName("NONE")), PathOf(server, NewName("NEW"))));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task MoveToNewName_KeepsLengthAndLastWriteTime(string server)
    {
        var client = Client(server);
        var path = PathOf(server, NewName("KEEP"));
        Write(client, path, "listed content");
        var session = await OpenSessionAsync(server);
        var before = session.GetEntry(path)!;
        var moved = SftpParkedName.PathFor(path);

        Assert.True(session.MoveToNewName(path, moved));

        var after = session.GetEntry(moved);
        Assert.NotNull(after);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.LastWriteTimeUtc.Ticks, after.LastWriteTimeUtc.Ticks);
        Assert.Null(session.GetEntry(path));
        client.DeleteFile(moved);
    }

    [Fact]
    public async Task MoveToNewName_UsesTheAtomicPosixRename_WhereOpenSshOffersIt()
    {
        // Seen from outside, the two requests differ only on an existing target: OpenSSH refuses
        // the plain rename there (link() meets the name) and carries out posix-rename@openssh.com
        // (rename(2)). A move that replaces the target therefore went through the POSIX request,
        // which has no gap between taking the new name and giving up the old one.
        var client = Client("openssh");
        var source = PathOf("openssh", NewName("SRC"));
        var target = PathOf("openssh", NewName("TGT"));
        Write(client, source, "moved");
        Write(client, target, "already there");

        Assert.Throws<SftpException>(() => client.RenameFile(source, target, false));
        var session = await OpenSessionAsync("openssh");
        Assert.True(session.MoveToNewName(source, target));

        Assert.Equal("moved", Read(client, target));
        Assert.False(client.Exists(source));
        client.DeleteFile(target);
    }

    [Fact]
    public async Task SftpGo_DoesNotOfferThePosixRename_SoTheMoveFallsBackToThePlainOne()
    {
        // What the session's fallback is for: SSH.NET refuses the POSIX request before sending it
        // when the server does not list the extension. The move still works, through the plain
        // request.
        var client = Client("sftpgo");
        var source = PathOf("sftpgo", NewName("SRC"));
        Write(client, source, "moved");

        Assert.Throws<NotSupportedException>(() => client.RenameFile(source, PathOf("sftpgo", NewName("TGT")), true));
        var session = await OpenSessionAsync("sftpgo");
        var moved = SftpParkedName.PathFor(source);
        Assert.True(session.MoveToNewName(source, moved));

        Assert.Equal("moved", Read(client, moved));
        client.DeleteFile(moved);
    }

    // ---- The node from end to end ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Node_IdentityUnchanged_DeletesTheFile_AndLeavesNothingAside(string server)
    {
        var client = Client(server);
        var name = NewName("EQ");
        Write(client, PathOf(server, name), "listed content");

        var error = await ListAndDeleteAsync(server, name);

        Assert.Null(error);
        Assert.False(client.Exists(PathOf(server, name)));
        Assert.Empty(LeftAside(client, server));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Node_FileReplacedBeforeTheRun_IsNotTouched(string server)
    {
        var client = Client(server);
        var name = NewName("REP");
        var path = PathOf(server, name);
        Write(client, path, "listed content");

        var error = await ListAndDeleteAsync(server, name, (operation, at) =>
        {
            if (operation == "GetEntry" && at == path)
            {
                Write(client, path, "a longer replacement delivered after the listing");
            }
        });

        Assert.IsType<MeshAdapterPipelineExecutionException>(error);
        Assert.Contains("is not the file that was listed", error.Message);
        Assert.Equal("a longer replacement delivered after the listing", Read(client, path));
        Assert.Empty(LeftAside(client, server));
        client.DeleteFile(path);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Node_FileDeliveredAgainAfterTheMove_KeepsTheNewFile(string server)
    {
        var client = Client(server);
        var name = NewName("NEW");
        var path = PathOf(server, name);
        Write(client, path, "listed content");

        var error = await ListAndDeleteAsync(server, name, (operation, at) =>
        {
            if (operation == "GetEntry" && at.Contains(".sftpdelete-", StringComparison.Ordinal))
            {
                Write(client, path, "delivered again");
            }
        });

        Assert.Null(error);
        Assert.Equal("delivered again", Read(client, path));
        Assert.Empty(LeftAside(client, server));
        client.DeleteFile(path);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Node_FileReplacedBetweenTheCheckAndTheMove_StaysAsideUnread(string server)
    {
        var client = Client(server);
        var name = NewName("MID");
        var path = PathOf(server, name);
        Write(client, path, "listed content");

        var error = await ListAndDeleteAsync(server, name, (operation, at) =>
        {
            if (operation == "MoveToNewName" && at == path)
            {
                Write(client, path, "a longer replacement in the moment before the move");
            }
        });

        Assert.IsType<MeshAdapterPipelineExecutionException>(error);
        Assert.Contains("was replaced between the check and the move", error.Message);
        var aside = Assert.Single(LeftAside(client, server));
        Assert.Equal("a longer replacement in the moment before the move", Read(client, aside));
        Assert.False(client.Exists(path));
        client.DeleteFile(aside);
    }

    /// <summary>A link under a name of its own pointing at a file next to it, made in the container.</summary>
    private async Task<(string Link, string Target)> LinkAsync(string server)
    {
        var client = Client(server);
        var target = NewName("TGT");
        var link = NewName("LNK");
        Write(client, PathOf(server, target), "the content the link points at");
        var made = await servers.Container(server).ExecAsync(
            ["ln", "-s", target, $"{SftpServersFixture.DirectoryInContainer(server)}/{link}"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, made.ExitCode);
        return (link, target);
    }

    private async Task<bool> IsLinkAsync(string server, string name)
    {
        var result = await servers.Container(server).ExecAsync(
            ["test", "-L", $"{SftpServersFixture.DirectoryInContainer(server)}/{name}"],
            TestContext.Current.CancellationToken);
        return result.ExitCode == 0;
    }

    private async Task RemoveAsync(string server, params string[] names)
    {
        // In the container: over SFTP the path of a link can resolve to its target.
        await servers.Container(server).ExecAsync(
            ["rm", "-f", .. names.Select(n => $"{SftpServersFixture.DirectoryInContainer(server)}/{n}")],
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Node_SymbolicLinkUnderTheListedName_OnOpenSsh_IsNotMovedOrDeleted()
    {
        // The listing reports the link itself. SSH.NET resolves every path with REALPATH before it
        // asks, and on OpenSSH that leads to the link's target, so the check under the name reads
        // the target: the identities differ and nothing is moved or deleted.
        var (link, target) = await LinkAsync("openssh");
        var client = Client("openssh");

        var error = await ListAndDeleteAsync("openssh", link);

        Assert.IsType<MeshAdapterPipelineExecutionException>(error);
        Assert.Contains("is not the file that was listed", error.Message);
        Assert.True(await IsLinkAsync("openssh", link));
        Assert.Equal("the content the link points at", Read(client, PathOf("openssh", target)));
        Assert.Empty(LeftAside(client, "openssh"));
        await RemoveAsync("openssh", link, target);
    }

    [Fact]
    public async Task Node_SymbolicLinkUnderTheListedName_OnSftpGo_DeletesTheLinkOnly()
    {
        // On SFTPGo the check, the move and the delete all address the link the listing reported:
        // the link goes, its target stays.
        var (link, target) = await LinkAsync("sftpgo");
        var client = Client("sftpgo");

        var error = await ListAndDeleteAsync("sftpgo", link);

        Assert.Null(error);
        Assert.False(await IsLinkAsync("sftpgo", link));
        Assert.Equal("the content the link points at", Read(client, PathOf("sftpgo", target)));
        Assert.Empty(LeftAside(client, "sftpgo"));
        await RemoveAsync("sftpgo", target);
    }

    [Theory]
    [MemberData(nameof(WithAndWithoutIdentity))]
    public async Task Node_SymbolicLinkWhoseTargetHasTheLinksLengthAndTime_OnOpenSsh_DeletesTheTarget(
        bool withIdentity)
    {
        // OpenSSH: SSH.NET resolves every path with REALPATH first, so the check, the move and the
        // delete address the link's target. Where the target happens to have the link's length (the
        // length of the link text) and its second, the check matches and the target is moved and
        // deleted; the link stays behind. Without an identity the plain delete removes the target
        // too.
        var client = Client("openssh");
        var target = NewName("TGT");
        var link = NewName("LNK");
        var directory = SftpServersFixture.DirectoryInContainer("openssh");
        Write(client, PathOf("openssh", target), new string('t', target.Length));
        var made = await servers.Container("openssh").ExecAsync(
            ["sh", "-c",
                $"ln -s {target} {directory}/{link} && touch -h -d '2020-02-03 04:05:06' {directory}/{link} && touch -d '2020-02-03 04:05:06' {directory}/{target}"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, made.ExitCode);

        var error = await ListAndDeleteAsync("openssh", link, withIdentity: withIdentity);

        Assert.Null(error);
        Assert.True(await IsLinkAsync("openssh", link));
        Assert.False(client.Exists(PathOf("openssh", target)));
        Assert.Empty(LeftAside(client, "openssh"));
        await RemoveAsync("openssh", link);
    }

    public static TheoryData<bool> WithAndWithoutIdentity => [true, false];

    // ---- The length of a name -----------------------------------------------------------------

    public static TheoryData<string, string> ServersAndFillers => new()
    {
        { "openssh", "A" },
        { "openssh", "Ä" },
        { "sftpgo", "A" },
        { "sftpgo", "Ä" }
    };

    /// <summary>A name of its own with exactly <paramref name="bytes" /> UTF-8 bytes, filled with <paramref name="filler" />.</summary>
    private static string NameOfBytes(int bytes, string filler)
    {
        var name = NewName("LIM")[..20];
        var fillers = (bytes - name.Length) / Encoding.UTF8.GetByteCount(filler);
        name += string.Concat(Enumerable.Repeat(filler, fillers));
        name += new string('A', bytes - Encoding.UTF8.GetByteCount(name));
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(name));
        return name;
    }

    [Theory]
    [MemberData(nameof(ServersAndFillers))]
    public async Task Node_TheLimitOfANameCountsBytes_210FitWithTheTemporaryName_211DoNot(string server, string filler)
    {
        // The temporary name adds 45 ASCII characters and both servers hold 255 bytes in a name, so
        // the limit for a file name is 210 bytes: 210 characters of ASCII, fewer with characters
        // that take two bytes in UTF-8, which SSH.NET sends by default.
        var client = Client(server);
        var fits = NameOfBytes(210, filler);
        var tooLong = NameOfBytes(211, filler);
        Write(client, PathOf(server, fits), "listed content");
        Write(client, PathOf(server, tooLong), "listed content");

        Assert.Null(await ListAndDeleteAsync(server, fits));
        var error = await ListAndDeleteAsync(server, tooLong);

        Assert.False(client.Exists(PathOf(server, fits)));
        Assert.IsType<MeshAdapterPipelineExecutionException>(error);
        Assert.Contains("Cannot move file", error.Message);
        Assert.Equal("listed content", Read(client, PathOf(server, tooLong)));
        Assert.Empty(LeftAside(client, server));
        client.DeleteFile(PathOf(server, tooLong));
    }

    // ---- SftpList@1 and the temporary name ----------------------------------------------------

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task List_SkipsAFileUnderTheTemporaryName_UnlessThePatternNamesIt(string server)
    {
        var client = Client(server);
        var name = NewName("LEFT");
        var parked = SftpParkedName.PathFor(PathOf(server, name));
        var parkedName = parked[(parked.LastIndexOf('/') + 1)..];
        Write(client, PathOf(server, name), "a delivery");
        Write(client, parked, "left under the temporary name");
        try
        {
            var all = await ListAsync(server, "*");
            var named = await ListAsync(server, ".*.sftpdelete-*");

            Assert.Contains(name, all.Names);
            Assert.DoesNotContain(parkedName, all.Names);
            A.CallTo(() => all.Logger.Warning(A<string>._, A<string>._,
                    A<string>.That.StartsWith("SftpList: skipping {0} file(s) in '{1}' under the temporary name"),
                    A<object[]>._))
                .MustHaveHappenedOnceExactly();
            Assert.Contains(parkedName, named.Names);
            Assert.DoesNotContain(name, named.Names);
            A.CallTo(() => named.Logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
                .MustNotHaveHappened();
        }
        finally
        {
            client.DeleteFile(parked);
            client.DeleteFile(PathOf(server, name));
        }
    }

    private async Task<(IReadOnlyList<string> Names, IPipelineLogger Logger)> ListAsync(string server,
        string filePattern)
    {
        var (etlContext, root, data, logger) = ContextWithLogger(server);
        await new SftpListNode(A.Fake<NodeDelegate>(), etlContext, new SshNetSftpSessionFactory())
            .ProcessObjectAsync(data, root.RegisterChildNode("SftpList", 0, new SftpListNodeConfiguration
            {
                ServerConfiguration = ServerConfig,
                RemoteDirectory = SftpServersFixture.Directory(server),
                FilePattern = filePattern,
                TargetPath = "$.files"
            }, data));
        var files = data.Get<JsonArray>("$.files");
        Assert.NotNull(files);
        return (files.Select(f => f!["name"]!.GetValue<string>()).ToList(), logger);
    }

    /// <summary>Opens real sessions and runs a hook before the delete node's session operations.</summary>
    private sealed class HookedFactory(ISftpSessionFactory inner, Action<string, string>? before)
        : ISftpSessionFactory
    {
        public async Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
            IMeshEtlContext etlContext, INodeContext nodeContext, CancellationToken cancellationToken = default)
        {
            var session = await inner.ConnectAsync(settings, serverConfigurationName, etlContext, nodeContext,
                cancellationToken);
            return new HookedSession(session, before);
        }
    }

    private sealed class HookedSession(ISftpSession inner, Action<string, string>? before) : ISftpSession
    {
        public IReadOnlyList<SftpEntry> List(string remoteDirectory) => inner.List(remoteDirectory);

        public SftpEntry? GetEntry(string remotePath)
        {
            before?.Invoke(nameof(GetEntry), remotePath);
            return inner.GetEntry(remotePath);
        }

        public bool MoveToNewName(string remotePath, string newRemotePath)
        {
            before?.Invoke(nameof(MoveToNewName), remotePath);
            return inner.MoveToNewName(remotePath, newRemotePath);
        }

        public byte[] Download(string remotePath, long maxBytes) => inner.Download(remotePath, maxBytes);

        public void Upload(Stream content, string remotePath) => inner.Upload(content, remotePath);

        public void EnsureDirectory(string remoteDirectory) => inner.EnsureDirectory(remoteDirectory);

        public bool Delete(string remotePath)
        {
            before?.Invoke(nameof(Delete), remotePath);
            return inner.Delete(remotePath);
        }

        public void Dispose() => inner.Dispose();
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// <c>SftpDelete@1</c> with the identity of the listed file (AB#8284): the identity is read
/// under the listed name first and a different file is left alone; only a matching file is moved
/// to a name only this run knows, checked again there and deleted there; nothing is ever renamed
/// back. Every case runs against <see cref="InMemorySftpServer" />, every case that reaches the
/// server asserts what is left on it, and the identity is read from a real data context, so the
/// values travel the way a pipeline hands them over.
/// </summary>
public sealed class SftpDeleteNodeIdentityTests : IDisposable
{
    private const string ServerConfig = "sftp-server-1";
    private const string RemoteFile = "/out/AR00001.TXT";
    private const long ListedLength = 1234;
    private const string ListedTimeText = "2026-11-05T09:58:12.0000000Z";
    private const string ListedIdentity = "length 1234, last write time 2026-11-05T09:58:12.0000000Z";
    private const string ParkedNamePattern = @"^/out/\.AR00001\.TXT\.sftpdelete-[0-9a-f]{32}$";

    private static readonly DateTime ListedTime = new(2026, 11, 5, 9, 58, 12, DateTimeKind.Utc);
    private static readonly RemoteFile Listed = new(ListedLength, ListedTime, "listed content");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly InMemorySftpServer _server = new();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly List<IDisposable> _disposables = [];

    public SftpDeleteNodeIdentityTests()
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).ReturnsLazily(() => Task.FromResult(_server.OpenSession()));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>The element a <c>ForEach@1</c> over an <c>SftpList@1</c> result hands to the loop body.</summary>
    private static JsonObject ListedElement(JsonNode? length, JsonNode? lastWriteTimeUtc)
    {
        return new JsonObject
        {
            ["key"] = new JsonObject
            {
                ["fullPath"] = RemoteFile,
                ["length"] = length,
                ["lastWriteTimeUtc"] = lastWriteTimeUtc
            }
        };
    }

    private static JsonObject ListedElement()
    {
        return ListedElement(ListedLength, ListedTimeText);
    }

    private static SftpDeleteNodeConfiguration WithListedIdentity(
        IdentityMismatchHandling onIdentityMismatch = IdentityMismatchHandling.Fail,
        MissingFileHandling onMissingFile = MissingFileHandling.Fail)
    {
        return new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = "$.key.fullPath",
            ExpectedLengthPath = "$.key.length",
            ExpectedLastWriteTimeUtcPath = "$.key.lastWriteTimeUtc",
            OnIdentityMismatch = onIdentityMismatch,
            OnMissingFile = onMissingFile
        };
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next, IPipelineLogger Logger)
        Prepare(SftpDeleteNodeConfiguration config, JsonNode? data = null, IPipelineExecutionMode? executionMode = null,
            IPipelineDebugger? debugger = null)
    {
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(JsonDocument.Parse((data ?? ListedElement()).ToJsonString()));
        _disposables.Add(dataContext);
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger,
            dataContext, debugger, executionMode, null);
        var nodeContext = root.RegisterChildNode("SftpDelete", 0, config, dataContext);
        return (dataContext, nodeContext, A.Fake<NodeDelegate>(), logger);
    }

    private Task RunAsync(IDataContext dataContext, INodeContext nodeContext, NodeDelegate next)
    {
        return new SftpDeleteNode(next, _etlContext, _sessionFactory).ProcessObjectAsync(dataContext, nodeContext);
    }

    /// <summary>The name the node moved the file to, read from the server's log.</summary>
    private string ParkedPath()
    {
        var move = Assert.Single(_server.Operations,
            o => o.StartsWith($"Move {RemoteFile} -> ", StringComparison.Ordinal));
        var parked = move[$"Move {RemoteFile} -> ".Length..];
        Assert.Matches(ParkedNamePattern, parked);
        return parked;
    }

    private static bool IsParked(string path)
    {
        return Regex.IsMatch(path, ParkedNamePattern);
    }

    private void AssertConnectedNever()
    {
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static void AssertWarned(IPipelineLogger logger, string fragment)
    {
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>.That.Contains(fragment), A<object[]>._))
            .MustHaveHappenedOnceExactly();
    }

    private static string Identity(long length, DateTime time)
    {
        return
            $"length {length}, last write time {time.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)}";
    }

    // The texts for a file that may be under the temporary name, word for word: each says what may
    // be under that name and what is safe to do with it, and none advises anything that could
    // replace a file under the listed name.

    private static string ListedFileNotDeletedText(INodeContext nodeContext, string parked, string failure)
    {
        return
            $"[{nodeContext.NodePath}]: Cannot delete file '{RemoteFile}' via SFTP: it was moved aside to '{parked}' and checked there - it is the file that was listed - but deleting it failed, and if only the answer was lost it is gone already: {failure}. SftpList@1 lists it there only for a pattern that contains '.sftpdelete-'; deleting it there finishes what this node started. Rename it back only if it is to be processed again, and to '{RemoteFile}' only while '{RemoteFile}' is free.";
    }

    private static string ReplacedFileLeftAsideText(INodeContext nodeContext, string parked, string found)
    {
        return
            $"[{nodeContext.NodePath}]: Remote file '{RemoteFile}' was moved aside to '{parked}', and the file there does not have the identity that was listed (expected {ListedIdentity}, found {found}). Either it was replaced between the check and the move - then it has not been read - or this server changes the length or the last write time of a file it renames - then it is the file that was listed. It stays under '{parked}', where SftpList@1 lists it only for a pattern that contains '.sftpdelete-', and nothing was deleted. Compare it with what this run processed before you delete it; to have it processed, give it a free name - '{RemoteFile}' only while '{RemoteFile}' is free.";
    }

    private static string MovedAsideNotText(INodeContext nodeContext, string parked, string failure)
    {
        return
            $"[{nodeContext.NodePath}]: Cannot move file '{RemoteFile}' aside to '{parked}' via SFTP before deleting it: {failure}. Nothing was deleted. Possible causes include a missing right to rename in the file's directory and a file name the server cannot hold with the 45 characters the temporary name adds. If the connection broke after the server carried out the move, the file is under '{parked}': compare it with the listing (expected {ListedIdentity}) before deleting it, and to have it processed give it a free name - '{RemoteFile}' only while '{RemoteFile}' is free.";
    }

    private static string UncheckedFileText(INodeContext nodeContext, string parked, string failure)
    {
        return
            $"[{nodeContext.NodePath}]: Cannot delete file '{RemoteFile}' via SFTP: it was moved aside to '{parked}', and reading it there failed: {failure}. Whether '{parked}' holds the file that was listed is unknown, and nothing was deleted. Compare it with the listing (expected {ListedIdentity}) before deleting it; to have it processed, give it a free name - '{RemoteFile}' only while '{RemoteFile}' is free.";
    }

    // ---- 1. The identity matches ---------------------------------------------------------------

    [Fact]
    public async Task IdentityMatches_DeletesTheMovedFileOnly()
    {
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, logger) = Prepare(WithListedIdentity());

        await RunAsync(dataContext, nodeContext, next);

        var parked = ParkedPath();
        Assert.Empty(_server.Files);
        // Deleted is the moved file, never the listed name: that is what leaves no gap for a file
        // delivered again under that name.
        Assert.Equal([
            $"GetEntry {RemoteFile}", $"Move {RemoteFile} -> {parked}", $"GetEntry {parked}", $"Delete {parked}",
            "Dispose"
        ], _server.Operations);
        A.CallTo(() => logger.Info(A<string>._, A<string>._, A<string>.That.Contains("removed"), A<object[]>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IdentityMatches_StaticValues_DeletesTheFile()
    {
        _server.Files[RemoteFile] = Listed;
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            ExpectedLength = ListedLength,
            ExpectedLastWriteTimeUtc = ListedTimeText
        };
        var (dataContext, nodeContext, next, _) = Prepare(config, new JsonObject());

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IdentityPathsTakePrecedenceOverStaticValues()
    {
        _server.Files[RemoteFile] = Listed;
        var config = WithListedIdentity();
        // Wrong on purpose: a configured path decides alone, as RemotePathPath does.
        config.ExpectedLength = 1;
        config.ExpectedLastWriteTimeUtc = "2000-01-01T00:00:00Z";
        var (dataContext, nodeContext, next, _) = Prepare(config);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LengthZero_FromThePathAndStatic_IsComparedAndDeleted(bool fromThePath)
    {
        // An empty file is a real delivery; zero is a length like any other.
        _server.Files[RemoteFile] = Listed with { Length = 0 };
        var config = fromThePath
            ? WithListedIdentity()
            : new SftpDeleteNodeConfiguration
            {
                ServerConfiguration = ServerConfig,
                RemotePath = RemoteFile,
                ExpectedLength = 0,
                ExpectedLastWriteTimeUtc = ListedTimeText
            };
        var (dataContext, nodeContext, next, _) = Prepare(config, ListedElement(0, ListedTimeText));

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    // ---- 2. Replaced before the node ran: nothing is moved --------------------------------------

    public static TheoryData<long, long> ReplacedIdentities()
    {
        var data = new TheoryData<long, long>
        {
            { ListedLength + 1, 0 },
            { ListedLength - 1, 0 },
            // One second and one tick: the comparison is on the instant as the listing writes it,
            // not on a rounded value.
            { ListedLength, TimeSpan.TicksPerSecond },
            { ListedLength, 1 },
            { ListedLength, -1 }
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(ReplacedIdentities))]
    public async Task FileReplacedBeforeTheRun_Fail_IsNotTouched_AndFailsNamingBothIdentities(long length,
        long tickShift)
    {
        var replaced = new RemoteFile(length, ListedTime.AddTicks(tickShift), "delivered again");
        _server.Files[RemoteFile] = replaced;
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = replaced }, _server.Files);
        Assert.Equal([$"GetEntry {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Equal(
            $"[{nodeContext.NodePath}]: Remote file '{RemoteFile}' is not the file that was listed: expected {ListedIdentity}, found {Identity(length, ListedTime.AddTicks(tickShift))}. It was not touched and stays under its name. OnIdentityMismatch is 'Fail'; use 'Warn' where a file delivered again under the same name is an expected event.",
            ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [MemberData(nameof(ReplacedIdentities))]
    public async Task FileReplacedBeforeTheRun_Warn_IsNotTouched_WarnsAndContinues(long length, long tickShift)
    {
        var replaced = new RemoteFile(length, ListedTime.AddTicks(tickShift), "delivered again");
        _server.Files[RemoteFile] = replaced;
        var (dataContext, nodeContext, next, logger) = Prepare(WithListedIdentity(IdentityMismatchHandling.Warn));

        await RunAsync(dataContext, nodeContext, next);

        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = replaced }, _server.Files);
        Assert.Equal([$"GetEntry {RemoteFile}", "Dispose"], _server.Operations);
        AssertWarned(logger, "is not the file that was listed");
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EntryIsADirectoryWithTheSameLengthAndTime_IsNotMoved()
    {
        var directory = new RemoteFile(ListedLength, ListedTime, IsDirectory: true);
        _server.Files[RemoteFile] = directory;
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = directory }, _server.Files);
        Assert.Equal([$"GetEntry {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Contains("found a directory", ex.Message);
    }

    // ---- 3. Acceptance criterion 2: delivered again after the move -------------------------------

    [Theory]
    [InlineData("GetEntry")]
    [InlineData("Delete")]
    public async Task FileDeliveredAgainAfterTheMove_OnlyTheListedOneIsDeleted(string step)
    {
        _server.Files[RemoteFile] = Listed;
        var deliveredAgain = new RemoteFile(ListedLength + 10, ListedTime.AddMinutes(5), "delivered again");
        _server.Before = (operation, path) =>
        {
            if (operation == step && IsParked(path) && !_server.Files.ContainsKey(RemoteFile))
            {
                _server.Files[RemoteFile] = deliveredAgain;
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        await RunAsync(dataContext, nodeContext, next);

        // The new delivery is untouched under its name; the listed file is gone.
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = deliveredAgain }, _server.Files);
        Assert.DoesNotContain($"Delete {RemoteFile}", _server.Operations);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    // ---- 4. Replaced between the check and the move ---------------------------------------------

    [Theory]
    [InlineData(IdentityMismatchHandling.Fail)]
    [InlineData(IdentityMismatchHandling.Warn)]
    public async Task FileReplacedBetweenTheCheckAndTheMove_StaysUnderTheTemporaryName_AndFails(
        IdentityMismatchHandling onIdentityMismatch)
    {
        _server.Files[RemoteFile] = Listed;
        var replaced = new RemoteFile(ListedLength + 1, ListedTime, "replaced");
        _server.Before = (operation, path) =>
        {
            if (operation == "MoveToNewName" && path == RemoteFile)
            {
                _server.Files[RemoteFile] = replaced;
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(onIdentityMismatch));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = ParkedPath();
        // Never renamed back: nothing is moved onto the listed name, so whatever arrives there
        // meanwhile cannot be replaced. Warn covers a file under its own name only.
        Assert.Equal(new Dictionary<string, RemoteFile> { [parked] = replaced }, _server.Files);
        Assert.DoesNotContain(_server.Operations, o => o.StartsWith($"Move {parked}", StringComparison.Ordinal));
        Assert.Equal(ReplacedFileLeftAsideText(nodeContext, parked, Identity(ListedLength + 1, ListedTime)),
            ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReplacedBeforeTheMove_AndDeliveredAgainAfterIt_KeepsBoth()
    {
        _server.Files[RemoteFile] = Listed;
        var replaced = new RemoteFile(ListedLength + 1, ListedTime, "replaced");
        var newest = new RemoteFile(99, ListedTime.AddMinutes(7), "newest");
        _server.Before = (operation, path) =>
        {
            if (operation == "MoveToNewName" && path == RemoteFile)
            {
                _server.Files[RemoteFile] = replaced;
            }
            else if (operation == "GetEntry" && IsParked(path))
            {
                _server.Files[RemoteFile] = newest;
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(IdentityMismatchHandling.Warn));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = ParkedPath();
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = newest, [parked] = replaced }, _server.Files);
        Assert.Equal(ReplacedFileLeftAsideText(nodeContext, parked, Identity(ListedLength + 1, ListedTime)),
            ex.Message);
    }

    // ---- 5. Failures before and after the move ---------------------------------------------------

    [Fact]
    public async Task CheckUnderTheListedNameFails_MovesNothing_AndIsAPlainDeleteFailure()
    {
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == "GetEntry" && path == RemoteFile)
            {
                throw new InvalidOperationException("permission denied");
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        Assert.Equal(["Dispose"], _server.Operations);
        Assert.Equal($"[{nodeContext.NodePath}]: Cannot delete file '{RemoteFile}' via SFTP: permission denied",
            ex.Message);
    }

    [Fact]
    public async Task MovingAsideFails_DeletesNothing_AndNamesBothPaths()
    {
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == "MoveToNewName" && path == RemoteFile)
            {
                throw new InvalidOperationException("permission denied.");
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = _server.LastMoveTarget!;
        Assert.Matches(ParkedNamePattern, parked);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        Assert.Equal([$"GetEntry {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Equal(
            MovedAsideNotText(nodeContext, parked, "permission denied"),
            ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingTheMovedFileFails_LeavesItThere_AndSaysItIsUnchecked(bool connectionBreaks)
    {
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == "GetEntry" && IsParked(path))
            {
                if (connectionBreaks)
                {
                    _server.Disconnected = true;
                }
                else
                {
                    throw new InvalidOperationException("permission denied");
                }
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(IdentityMismatchHandling.Warn));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = ParkedPath();
        Assert.Equal(new Dictionary<string, RemoteFile> { [parked] = Listed }, _server.Files);
        Assert.Equal(
            UncheckedFileText(nodeContext, parked, connectionBreaks ? "Connection lost" : "permission denied"),
            ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingTheMovedFileFails_LeavesItThere_AndSaysItIsTheListedFile(bool connectionBreaks)
    {
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == "Delete" && IsParked(path))
            {
                if (connectionBreaks)
                {
                    _server.Disconnected = true;
                }
                else
                {
                    throw new InvalidOperationException("permission denied");
                }
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(IdentityMismatchHandling.Warn));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = ParkedPath();
        Assert.Equal(new Dictionary<string, RemoteFile> { [parked] = Listed }, _server.Files);
        Assert.Equal(
            ListedFileNotDeletedText(nodeContext, parked, connectionBreaks ? "Connection lost" : "permission denied"),
            ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    // A request the server carried out whose answer was lost: the node sees a failure, the server
    // has done it.

    [Fact]
    public async Task MoveCarriedOut_AnswerLost_LeavesTheFileUnderTheTemporaryName_AndNamesIt()
    {
        _server.Files[RemoteFile] = Listed;
        _server.After = (operation, path) =>
        {
            if (operation == "MoveToNewName" && path == RemoteFile)
            {
                throw new InvalidOperationException("Connection lost.");
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var parked = ParkedPath();
        Assert.Equal(new Dictionary<string, RemoteFile> { [parked] = Listed }, _server.Files);
        Assert.Equal([$"GetEntry {RemoteFile}", $"Move {RemoteFile} -> {parked}", "Dispose"], _server.Operations);
        Assert.Equal(MovedAsideNotText(nodeContext, parked, "Connection lost"), ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DeleteCarriedOut_AnswerLost_LeavesNothing_AndSaysItWasTheListedFile()
    {
        _server.Files[RemoteFile] = Listed;
        _server.After = (operation, path) =>
        {
            if (operation == "Delete" && IsParked(path))
            {
                throw new InvalidOperationException("Connection lost.");
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Empty(_server.Files);
        Assert.Equal(ListedFileNotDeletedText(nodeContext, ParkedPath(), "Connection lost"), ex.Message);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    // ---- 6. The file is missing -----------------------------------------------------------------

    [Fact]
    public async Task FileMissing_Fail_FailsAsToday()
    {
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(
            MeshAdapterPipelineExecutionException.SftpFileNotFound(nodeContext, RemoteFile).Message, ex.Message);
        Assert.Equal([$"GetEntry {RemoteFile}", "Dispose"], _server.Operations);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FileMissing_Ignore_WarnsAndContinuesAsToday()
    {
        var (dataContext, nodeContext, next, logger) =
            Prepare(WithListedIdentity(onMissingFile: MissingFileHandling.Ignore));

        await RunAsync(dataContext, nodeContext, next);

        AssertWarned(logger, "does not exist any more");
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("MoveToNewName", MissingFileHandling.Fail)]
    [InlineData("MoveToNewName", MissingFileHandling.Ignore)]
    [InlineData("GetEntry", MissingFileHandling.Fail)]
    [InlineData("GetEntry", MissingFileHandling.Ignore)]
    [InlineData("Delete", MissingFileHandling.Fail)]
    [InlineData("Delete", MissingFileHandling.Ignore)]
    public async Task FileVanishesAfterTheCheck_FollowsOnMissingFile(string step, MissingFileHandling onMissingFile)
    {
        // Before the move the file goes from the listed name - another run took it - after the
        // move from the temporary one.
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == step && (step == "MoveToNewName" ? path == RemoteFile : IsParked(path)))
            {
                _server.Files.Remove(path);
            }
        };
        var (dataContext, nodeContext, next, logger) = Prepare(WithListedIdentity(onMissingFile: onMissingFile));

        if (onMissingFile == MissingFileHandling.Fail)
        {
            var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
                () => RunAsync(dataContext, nodeContext, next));
            Assert.Equal(
                MeshAdapterPipelineExecutionException.SftpFileNotFound(nodeContext, RemoteFile).Message, ex.Message);
        }
        else
        {
            await RunAsync(dataContext, nodeContext, next);
            AssertWarned(logger, "does not exist any more");
        }

        Assert.Empty(_server.Files);
    }

    [Theory]
    [InlineData(MissingFileHandling.Fail)]
    [InlineData(MissingFileHandling.Ignore)]
    public async Task FileVanishesBeforeTheMove_EndsTheRun_AndReadsNothingUnderTheTemporaryName(
        MissingFileHandling onMissingFile)
    {
        _server.Files[RemoteFile] = Listed;
        _server.Before = (operation, path) =>
        {
            if (operation == "MoveToNewName" && path == RemoteFile)
            {
                _server.Files.Remove(path);
            }
        };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(onMissingFile: onMissingFile));

        await Record.ExceptionAsync(() => RunAsync(dataContext, nodeContext, next));

        // A move that reported the source as missing ends the run: nothing is read or deleted
        // under the temporary name.
        Assert.Equal([$"GetEntry {RemoteFile}", $"Move {RemoteFile} -> {_server.LastMoveTarget}", "Dispose"],
            _server.Operations);
    }

    // ---- 7. Without an identity nothing changes ------------------------------------------------

    [Fact]
    public async Task WithoutIdentity_DeletesByNameAsToday_AndNeverMovesOrReadsAttributes()
    {
        _server.Files[RemoteFile] = Listed;
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = ServerConfig, RemotePath = RemoteFile };
        var (dataContext, nodeContext, next, _) = Prepare(config, new JsonObject());

        await RunAsync(dataContext, nodeContext, next);

        Assert.Equal([$"Delete {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Empty(_server.Files);
    }

    // ---- 8. Incomplete, blank or unusable identity ----------------------------------------------

    public static TheoryData<string> PartialIdentities =>
    [
        nameof(SftpDeleteNodeConfiguration.ExpectedLength),
        nameof(SftpDeleteNodeConfiguration.ExpectedLengthPath),
        nameof(SftpDeleteNodeConfiguration.ExpectedLastWriteTimeUtc),
        nameof(SftpDeleteNodeConfiguration.ExpectedLastWriteTimeUtcPath)
    ];

    [Theory]
    [MemberData(nameof(PartialIdentities))]
    public async Task PartialIdentity_IsRefusedBeforeConnecting(string onlyProperty)
    {
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = ServerConfig, RemotePath = RemoteFile };
        switch (onlyProperty)
        {
            case nameof(SftpDeleteNodeConfiguration.ExpectedLength):
                config.ExpectedLength = ListedLength;
                break;
            case nameof(SftpDeleteNodeConfiguration.ExpectedLengthPath):
                config.ExpectedLengthPath = "$.key.length";
                break;
            case nameof(SftpDeleteNodeConfiguration.ExpectedLastWriteTimeUtc):
                config.ExpectedLastWriteTimeUtc = ListedTimeText;
                break;
            default:
                config.ExpectedLastWriteTimeUtcPath = "$.key.lastWriteTimeUtc";
                break;
        }

        var missing = onlyProperty.StartsWith("ExpectedLength", StringComparison.Ordinal)
            ? "ExpectedLastWriteTimeUtc"
            : "ExpectedLength";
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
        Assert.Contains($"{missing} or {missing}Path", ex.Message);
        AssertConnectedNever();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PartialIdentity_IsRefusedInADryRunToo()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            ExpectedLength = ListedLength
        };
        var (dataContext, nodeContext, next, _) = Prepare(config,
            executionMode: new DefaultPipelineExecutionMode { IsDryRun = true });

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() => RunAsync(dataContext, nodeContext, next));

        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(" ", " ", nameof(SftpDeleteNodeConfiguration.ExpectedLengthPath))]
    [InlineData("", "", nameof(SftpDeleteNodeConfiguration.ExpectedLengthPath))]
    [InlineData("\t", "$.key.lastWriteTimeUtc", nameof(SftpDeleteNodeConfiguration.ExpectedLengthPath))]
    [InlineData("$.key.length", " ", nameof(SftpDeleteNodeConfiguration.ExpectedLastWriteTimeUtcPath))]
    public async Task BlankIdentityPaths_AreRefused_NotTreatedAsUnset(string lengthPath, string timePath,
        string blankProperty)
    {
        _server.Files[RemoteFile] = Listed;
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            ExpectedLengthPath = lengthPath,
            ExpectedLastWriteTimeUtcPath = timePath
        };
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(
            $"[{nodeContext.NodePath}]: {blankProperty} is set but blank. Name the path of the value in the data context; a blank path is not read as \"no check\". Nothing was deleted.",
            ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        AssertConnectedNever();
    }

    [Fact]
    public async Task BlankLengthPathWithAStaticLength_IsRefused_AndDoesNotFallBack()
    {
        var config = WithListedIdentity();
        config.ExpectedLength = ListedLength;
        config.ExpectedLengthPath = " ";
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains("ExpectedLengthPath is set but blank", ex.Message);
        AssertConnectedNever();
    }

    public static TheoryData<string> UnusableLengths =>
    [
        "null",
        "\"\"",
        "\"   \"",
        "\"12a\"",
        "\" 12\"",
        "\"12 \"",
        "\"+12\"",
        "\"-12\"",
        "\"1e3\"",
        "\"1,234\"",
        "-1",
        "12.5",
        "1234.0",
        "true",
        "{}",
        "[]"
    ];

    [Theory]
    [MemberData(nameof(UnusableLengths))]
    public async Task UnusableLengthFromTheDataContext_IsRefusedBeforeConnecting(string lengthJson)
    {
        var data = ListedElement(JsonNode.Parse(lengthJson), ListedTimeText);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
        Assert.Contains("ExpectedLengthPath '$.key.length'", ex.Message);
        AssertConnectedNever();
    }

    [Fact]
    public async Task LengthPathResolvingToNothing_IsRefusedAndDoesNotFallBackToTheStaticValue()
    {
        var data = new JsonObject
        {
            ["key"] = new JsonObject { ["fullPath"] = RemoteFile, ["lastWriteTimeUtc"] = ListedTimeText }
        };
        var config = WithListedIdentity();
        config.ExpectedLength = ListedLength;
        var (dataContext, nodeContext, next, _) = Prepare(config, data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains("ExpectedLengthPath '$.key.length' resolved to no value", ex.Message);
        AssertConnectedNever();
    }

    [Fact]
    public async Task NegativeStaticLength_IsRefusedBeforeConnecting()
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            ExpectedLength = -1,
            ExpectedLastWriteTimeUtc = ListedTimeText
        };
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains("ExpectedLength resolved to -1", ex.Message);
        AssertConnectedNever();
    }

    [Theory]
    [InlineData(33)]
    [InlineData(200)]
    public async Task UnusableTextLongerThan32Characters_IsNotShown_OnlyItsLength(int length)
    {
        // A path pointing at the wrong node can hand over a whole file content. The longest valid
        // value, a time with seven fractional digits, has 28 characters; a longer text is named by
        // its length only, so no content reaches the message and the run's log.
        var content = "x" + new string('y', length - 1);
        var data = ListedElement(content, ListedTimeText);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(
            $"[{nodeContext.NodePath}]: ExpectedLengthPath '$.key.length' resolved to a text of {length} characters, but the expected identity needs a length of zero or more bytes, as a JSON integer or a string of digits. Nothing was deleted, because the file cannot be checked.",
            ex.Message);
        Assert.DoesNotContain("xy", ex.Message);
    }

    [Fact]
    public async Task UnusableValueOf32Characters_IsShownWhole()
    {
        var content = new string('x', 32);
        var data = ListedElement(content, ListedTimeText);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains($"resolved to '{content}', but", ex.Message);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("\"1234\"")]
    [InlineData("\"001234\"")]
    public async Task LengthAsJsonIntegerOrDigitString_IsAccepted(string lengthJson)
    {
        _server.Files[RemoteFile] = Listed;
        var data = ListedElement(JsonNode.Parse(lengthJson), ListedTimeText);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    [Fact]
    public async Task LengthBeyondInt32_IsCompared()
    {
        const long large = 5_000_000_000;
        _server.Files[RemoteFile] = Listed with { Length = large };
        var data = ListedElement(large, ListedTimeText);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    public static TheoryData<string> UnusableTimes =>
    [
        "null",
        "\"\"",
        "\"   \"",
        "12345",
        "true",
        "\"2026-11-05T09:58:12\"",
        "\"2026-11-05T09:58:12z\"",
        "\"2026-11-05t09:58:12Z\"",
        "\"2026-11-05 09:58:12Z\"",
        "\"2026-11-05T09:58:12+00:00\"",
        "\"2026-11-05T10:58:12+01:00\"",
        "\"2026-11-05T09:58:12GMT\"",
        "\"2026-11-05T09:58:12 Z\"",
        "\"2026-11-05T09:58:12.Z\"",
        "\"2026-11-05T09:58:12.12345678Z\"",
        "\"2026-11-05T09:58:12Z\\n\"",
        "\" 2026-11-05T09:58:12Z\"",
        "\"2026-11-05T09:58Z\"",
        "\"2026-13-05T09:58:12Z\"",
        "\"2026-11-31T09:58:12Z\"",
        "\"2026-11-05T24:00:00Z\"",
        "\"05.11.2026 09:58:12\"",
        "\"\\u0662026-11-05T09:58:12Z\""
    ];

    [Theory]
    [MemberData(nameof(UnusableTimes))]
    public async Task UnusableTimeFromTheDataContext_IsRefusedBeforeConnecting(string timeJson)
    {
        var data = ListedElement(ListedLength, JsonNode.Parse(timeJson));
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.StartsWith($"[{nodeContext.NodePath}]", ex.Message);
        Assert.Contains("ExpectedLastWriteTimeUtcPath '$.key.lastWriteTimeUtc'", ex.Message);
        AssertConnectedNever();
    }

    [Theory]
    [InlineData("2026-11-05T09:58:12")]
    [InlineData("2026-11-05T09:58:12+00:00")]
    [InlineData("")]
    public async Task UnusableStaticTime_IsRefusedBeforeConnecting(string time)
    {
        var config = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            ExpectedLength = ListedLength,
            ExpectedLastWriteTimeUtc = time
        };
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains($"ExpectedLastWriteTimeUtc resolved to '{time}'", ex.Message);
        AssertConnectedNever();
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", true)]
    [InlineData("0001-01-01T00:00:00.0000000Z", true)]
    [InlineData("0001-01-01T00:00:00Z", false)]
    [InlineData("0001-01-01T00:00:00.0000000Z", false)]
    public async Task TimeOfAServerThatReportedNone_IsRefusedBeforeConnecting(string time, bool fromThePath)
    {
        // SftpList@1 writes 0001-01-01 for every file of a server that sends no modification time;
        // accepted, it would match every file and leave the length as the only check.
        _server.Files[RemoteFile] = Listed with { LastWriteTimeUtc = DateTime.MinValue };
        var config = fromThePath
            ? WithListedIdentity()
            : new SftpDeleteNodeConfiguration
            {
                ServerConfiguration = ServerConfig,
                RemotePath = RemoteFile,
                ExpectedLength = ListedLength,
                ExpectedLastWriteTimeUtc = time
            };
        var (dataContext, nodeContext, next, _) = Prepare(config, ListedElement(ListedLength, time));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        var source = fromThePath ? "ExpectedLastWriteTimeUtcPath '$.key.lastWriteTimeUtc'" : "ExpectedLastWriteTimeUtc";
        Assert.Equal(
            $"[{nodeContext.NodePath}]: {source} resolved to '{time}': 0001-01-01T00:00:00Z means the server reported no last write time, and the check needs one. Nothing was deleted, because the file cannot be checked.",
            ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed with { LastWriteTimeUtc = DateTime.MinValue } },
            _server.Files);
        AssertConnectedNever();
    }

    [Fact]
    public async Task OneTickAfterTheTimeOfAServerThatReportedNone_IsARealTime()
    {
        var time = DateTime.MinValue.AddTicks(1);
        _server.Files[RemoteFile] = Listed with { LastWriteTimeUtc = time };
        var (dataContext, nodeContext, next, _) =
            Prepare(WithListedIdentity(), ListedElement(ListedLength, "0001-01-01T00:00:00.0000001Z"));

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    [Theory]
    [InlineData("2026-11-05T09:58:12Z")]
    [InlineData("2026-11-05T09:58:12.0Z")]
    [InlineData("2026-11-05T09:58:12.000Z")]
    [InlineData("2026-11-05T09:58:12.0000000Z")]
    public async Task TimeWithAnyNumberOfFractionalDigits_NamesTheSameInstant(string time)
    {
        _server.Files[RemoteFile] = Listed;
        var data = ListedElement(ListedLength, time);
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(), data);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    // ---- 9. What the identity cannot see --------------------------------------------------------

    [Fact]
    public async Task SameLengthAndTimeDifferentContent_IsDeleted_TheIdentityCannotSeeContent()
    {
        // The documented limit (AB#8284): the identity is length and last write time. A file
        // replaced by one of the same length within the server's time resolution reads as the
        // file that was listed.
        _server.Files[RemoteFile] = Listed with { Content = "other content of the same length" };
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity());

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
    }

    // ---- 10. The identity exactly as SftpList@1 writes it ----------------------------------------

    [Fact]
    public async Task IdentityAsSftpListEmitsIt_IsAccepted()
    {
        // Seven fractional digits, so a parser that drops them or a read that turns the text into
        // a DateTime first would no longer name the same instant.
        var time = ListedTime.AddTicks(1_234_567);
        _server.Files[RemoteFile] = Listed with { LastWriteTimeUtc = time };

        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        _disposables.Add(dataContext);
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger,
            dataContext, null, null, null);

        await ListAsync(dataContext, root, new SftpEntry("AR00001.TXT", RemoteFile, false, ListedLength, time));
        Assert.Equal("2026-11-05T09:58:12.1234567Z", dataContext.GetValue("$.files[0].lastWriteTimeUtc", false));

        var deleteConfig = new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = "$.files[0].fullPath",
            ExpectedLengthPath = "$.files[0].length",
            ExpectedLastWriteTimeUtcPath = "$.files[0].lastWriteTimeUtc"
        };
        var deleteContext = root.RegisterChildNode("SftpDelete", 1, deleteConfig, dataContext);
        var next = A.Fake<NodeDelegate>();
        await RunAsync(dataContext, deleteContext, next);

        Assert.Empty(_server.Files);
        A.CallTo(() => next(dataContext, deleteContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IdentityAsSftpListEmitsIt_DoesNotDependOnTheCurrentCulture()
    {
        // A culture whose calendar is not the Gregorian one: a list or a parse that reads the
        // current culture writes or expects the year 2569 for 2026, and the pair no longer names
        // the same instant.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            var time = ListedTime.AddTicks(1_234_567);
            _server.Files[RemoteFile] = Listed with { LastWriteTimeUtc = time };
            var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
            _disposables.Add(dataContext);
            var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                A.Fake<IPipelineLogger>(), dataContext, null, null, null);

            await ListAsync(dataContext, root, new SftpEntry("AR00001.TXT", RemoteFile, false, ListedLength, time));
            Assert.Equal("2026-11-05T09:58:12.1234567Z", dataContext.GetValue("$.files[0].lastWriteTimeUtc", false));

            var deleteContext = root.RegisterChildNode("SftpDelete", 1, new SftpDeleteNodeConfiguration
            {
                ServerConfiguration = ServerConfig,
                RemotePathPath = "$.files[0].fullPath",
                ExpectedLengthPath = "$.files[0].length",
                ExpectedLastWriteTimeUtcPath = "$.files[0].lastWriteTimeUtc"
            }, dataContext);
            await RunAsync(dataContext, deleteContext, A.Fake<NodeDelegate>());

            Assert.Empty(_server.Files);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>Runs the real <c>SftpList@1</c> over <paramref name="entries" /> into <c>$.files</c>.</summary>
    private async Task ListAsync(IDataContext dataContext, INodeContext root, params SftpEntry[] entries)
    {
        var listSession = A.Fake<ISftpSession>();
        A.CallTo(() => listSession.List("/out")).Returns(entries);
        var listFactory = A.Fake<ISftpSessionFactory>();
        A.CallTo(() => listFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).Returns(Task.FromResult(listSession));
        var listConfig = new SftpListNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = "/out",
            FilePattern = "AR*TXT",
            TargetPath = "$.files"
        };
        await new SftpListNode(A.Fake<NodeDelegate>(), _etlContext, listFactory).ProcessObjectAsync(dataContext,
            root.RegisterChildNode("SftpList", 0, listConfig, dataContext));
    }

    // ---- 11. Dry run ---------------------------------------------------------------------------

    private static (IPipelineDebugger Debugger, Func<JsonNode?> Intent) RecordingDebugger()
    {
        var debugger = A.Fake<IPipelineDebugger>();
        JsonNode? intent = null;
        A.CallTo(() => debugger.RecordDryRunIntent(A<string>._, A<NodePath>._, A<string?>._, A<uint>._,
                DryRunHonouredLoadNodes.SftpDelete, A<JsonNode?>._))
            .Invokes((string _, NodePath _, string? _, uint _, string _, JsonNode? data) => intent = data);
        return (debugger, () => intent);
    }

    [Fact]
    public async Task DryRun_RecordsTheIdentity_AndConnectsNothing()
    {
        _server.Files[RemoteFile] = Listed;
        var (debugger, intent) = RecordingDebugger();
        var (dataContext, nodeContext, next, _) = Prepare(WithListedIdentity(IdentityMismatchHandling.Warn),
            executionMode: new DefaultPipelineExecutionMode { IsDryRun = true }, debugger: debugger);

        await RunAsync(dataContext, nodeContext, next);

        AssertConnectedNever();
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        var recorded = intent();
        Assert.NotNull(recorded);
        Assert.Equal(RemoteFile, recorded["remotePath"]!.GetValue<string>());
        Assert.Equal(ListedLength, recorded["expectedLength"]!.GetValue<long>());
        Assert.Equal(ListedTimeText, recorded["expectedLastWriteTimeUtc"]!.GetValue<string>());
        Assert.Equal("Warn", recorded["onIdentityMismatch"]!.GetValue<string>());
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DryRunWithoutIdentity_RecordsTheSameIntentAsBefore()
    {
        var (debugger, intent) = RecordingDebugger();
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = ServerConfig, RemotePath = RemoteFile };
        var (dataContext, nodeContext, next, _) = Prepare(config,
            executionMode: new DefaultPipelineExecutionMode { IsDryRun = true }, debugger: debugger);

        await RunAsync(dataContext, nodeContext, next);

        var recorded = intent();
        Assert.NotNull(recorded);
        Assert.Equal(["host", "port", "username", "remotePath", "onMissingFile"],
            recorded.AsObject().Select(p => p.Key));
    }

    // ---- 12. Identity options written without a value ------------------------------------------

    private static async Task<SftpDeleteNodeConfiguration> LoadAsync(string lengthLine, string timeLine,
        string mismatchLine = "")
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
        var root = await serializer.DeserializeAsync($"""
                                                       transformations:
                                                         - type: SftpDelete@1
                                                           serverConfiguration: sftp-server-1
                                                           remotePath: {RemoteFile}
                                                           {lengthLine}
                                                           {timeLine}
                                                           {mismatchLine}
                                                       """);
        return Assert.IsType<SftpDeleteNodeConfiguration>(Assert.Single(root.Transformations!));
    }

    [Theory]
    [InlineData("expectedLengthPath:", "expectedLastWriteTimeUtcPath:")]
    [InlineData("expectedLengthPath: ~", "expectedLastWriteTimeUtcPath: ~")]
    [InlineData("expectedLengthPath: null", "expectedLastWriteTimeUtcPath: null")]
    [InlineData("expectedLength:", "expectedLastWriteTimeUtc:")]
    public async Task IdentityKeysWithoutAValue_AreYamlNull_AndCountAsNotSet(string lengthLine, string timeLine)
    {
        // The documented boundary: a key without a value, ~ and null are YAML null, which the node
        // cannot tell from a key that is left out - so the plain delete runs, here on a file the
        // check would have kept. Write both keys with a value or neither.
        var config = await LoadAsync(lengthLine, timeLine);
        Assert.Null(config.ExpectedLength);
        Assert.Null(config.ExpectedLengthPath);
        Assert.Null(config.ExpectedLastWriteTimeUtc);
        Assert.Null(config.ExpectedLastWriteTimeUtcPath);
        _server.Files[RemoteFile] = Listed with { Length = ListedLength + 1 };
        var (dataContext, nodeContext, next, _) = Prepare(config);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Equal([$"Delete {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Empty(_server.Files);
    }

    [Theory]
    [InlineData("expectedLengthPath: \"\"", "expectedLastWriteTimeUtcPath: \"\"", "ExpectedLengthPath is set but blank")]
    [InlineData("expectedLengthPath: \" \"", "expectedLastWriteTimeUtcPath: $.key.lastWriteTimeUtc", "ExpectedLengthPath is set but blank")]
    [InlineData("expectedLengthPath: $.key.length", "expectedLastWriteTimeUtcPath:", "ExpectedLastWriteTimeUtc or ExpectedLastWriteTimeUtcPath is not set")]
    [InlineData("expectedLengthPath:", "expectedLastWriteTimeUtcPath: $.key.lastWriteTimeUtc", "ExpectedLength or ExpectedLengthPath is not set")]
    public async Task EmptyTextOrOneKeyWithoutAValue_IsRefusedBeforeConnecting(string lengthLine, string timeLine,
        string reason)
    {
        // An empty or blank text is a value: it is refused, never read as "no check". One key with
        // a value and the other without is half an identity.
        var config = await LoadAsync(lengthLine, timeLine);
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains(reason, ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        AssertConnectedNever();
    }

    // ---- 13. Warn without an identity ----------------------------------------------------------

    private static string WarnWithoutIdentityText(INodeContext nodeContext)
    {
        return
            $"[{nodeContext.NodePath}]: OnIdentityMismatch is 'Warn', but no expected identity is configured, so the file would be deleted without a check. Set ExpectedLength or ExpectedLengthPath and ExpectedLastWriteTimeUtc or ExpectedLastWriteTimeUtcPath - a key written without a value counts as not set - or remove 'Warn'. Nothing was deleted.";
    }

    private static SftpDeleteNodeConfiguration WarnWithoutAnIdentity()
    {
        return new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = RemoteFile,
            OnIdentityMismatch = IdentityMismatchHandling.Warn
        };
    }

    [Fact]
    public async Task WarnWithoutIdentity_IsRefusedBeforeConnecting_AndSaysWhatToDo()
    {
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, _) = Prepare(WarnWithoutAnIdentity(), new JsonObject());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(WarnWithoutIdentityText(nodeContext), ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        AssertConnectedNever();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WarnWithoutIdentity_IsRefusedInADryRunToo_AndRecordsNoIntent()
    {
        var (debugger, intent) = RecordingDebugger();
        var (dataContext, nodeContext, next, _) = Prepare(WarnWithoutAnIdentity(), new JsonObject(),
            new DefaultPipelineExecutionMode { IsDryRun = true }, debugger);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(WarnWithoutIdentityText(nodeContext), ex.Message);
        Assert.Null(intent());
        AssertConnectedNever();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("expectedLengthPath:", "expectedLastWriteTimeUtcPath:")]
    [InlineData("expectedLengthPath: ~", "expectedLastWriteTimeUtcPath: ~")]
    [InlineData("expectedLengthPath: null", "expectedLastWriteTimeUtcPath: null")]
    [InlineData("expectedLength:", "expectedLastWriteTimeUtc:")]
    public async Task WarnWithIdentityKeysWithoutAValue_IsRefusedBeforeConnecting(string lengthLine, string timeLine)
    {
        // The same YAML null as IdentityKeysWithoutAValue_AreYamlNull_AndCountAsNotSet, but here the
        // definition states that it expects a check: deleting unchecked would ignore that.
        var config = await LoadAsync(lengthLine, timeLine, "onIdentityMismatch: Warn");
        Assert.Equal(IdentityMismatchHandling.Warn, config.OnIdentityMismatch);
        Assert.Null(config.ExpectedLength);
        Assert.Null(config.ExpectedLengthPath);
        Assert.Null(config.ExpectedLastWriteTimeUtc);
        Assert.Null(config.ExpectedLastWriteTimeUtcPath);
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Equal(WarnWithoutIdentityText(nodeContext), ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        AssertConnectedNever();
    }

    [Fact]
    public async Task FailWrittenOutWithoutIdentity_CannotBeToldFromTheDefault_AndRunsThePlainDelete()
    {
        var config = await LoadAsync("", "", "onIdentityMismatch: Fail");
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, _) = Prepare(config);

        await RunAsync(dataContext, nodeContext, next);

        Assert.Equal([$"Delete {RemoteFile}", "Dispose"], _server.Operations);
        Assert.Empty(_server.Files);
    }

    [Fact]
    public async Task WarnWithAStaticIdentity_IsAccepted()
    {
        _server.Files[RemoteFile] = Listed;
        var config = WarnWithoutAnIdentity();
        config.ExpectedLength = ListedLength;
        config.ExpectedLastWriteTimeUtc = ListedTimeText;
        var (dataContext, nodeContext, next, _) = Prepare(config, new JsonObject());

        await RunAsync(dataContext, nodeContext, next);

        Assert.Empty(_server.Files);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("expectedLengthPath: $.key.length", "", "ExpectedLastWriteTimeUtc or ExpectedLastWriteTimeUtcPath is not set")]
    [InlineData("expectedLengthPath: \"\"", "expectedLastWriteTimeUtcPath: \"\"", "ExpectedLengthPath is set but blank")]
    public async Task WarnWithHalfOrABlankIdentity_IsRefusedForThatReason(string lengthLine, string timeLine,
        string reason)
    {
        var config = await LoadAsync(lengthLine, timeLine, "onIdentityMismatch: Warn");
        _server.Files[RemoteFile] = Listed;
        var (dataContext, nodeContext, next, _) = Prepare(config);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RunAsync(dataContext, nodeContext, next));

        Assert.Contains(reason, ex.Message);
        Assert.Equal(new Dictionary<string, RemoteFile> { [RemoteFile] = Listed }, _server.Files);
        AssertConnectedNever();
    }

    // ---- 14. Inside a real ForEach@1 over an SftpList@1 result ---------------------------------

    private const string SecondFile = "/out/AR00002.TXT";
    private const string ParentFile = "/out/PARENT.TXT";
    private const long LengthBeyondInt32 = 5_000_000_000;
    private const long ParentLength = 999;

    private static readonly DateTime ParentTime = new(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>The loop of the developer guide, read by the real pipeline serializer.</summary>
    private static async Task<ForEachNodeConfiguration> LoadLoopAsync(string mismatchLine)
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
        var root = await serializer.DeserializeAsync($"""
                                                       transformations:
                                                         - type: ForEach@1
                                                           iterationPath: $.files
                                                           keyPath: $.current
                                                           targetPath: $.loopResult
                                                           maxDegreeOfParallelism: 1
                                                           transformations:
                                                             - type: SftpDelete@1
                                                               serverConfiguration: {ServerConfig}
                                                               remotePathPath: $.current.fullPath
                                                               expectedLengthPath: $.current.length
                                                               expectedLastWriteTimeUtcPath: $.current.lastWriteTimeUtc
                                                               onMissingFile: Ignore
                                                               {mismatchLine}
                                                       """);
        return Assert.IsType<ForEachNodeConfiguration>(Assert.Single(root.Transformations!));
    }

    /// <summary>
    /// Lists two files with the real <c>SftpList@1</c>, puts <paramref name="second" /> under the
    /// second name, and runs the real <c>ForEach@1</c> with the real <c>SftpDelete@1</c> as its
    /// child. The parent context holds another file's values under the loop's key path, and that
    /// file is on the server: an identity or a path read from the parent instead of the element
    /// fails the check of the first file or removes the parent's file.
    /// </summary>
    private async Task<(Exception? Error, NodeDelegate Next, IPipelineLogger Logger)> ListAndLoopAsync(
        string mismatchLine, RemoteFile second)
    {
        // Seven fractional digits and a length beyond Int32: read through the loop as anything but
        // the text and the number the listing wrote, they no longer name this file.
        var first = new RemoteFile(LengthBeyondInt32, ListedTime.AddTicks(1_234_567));
        _server.Files[RemoteFile] = first;
        _server.Files[SecondFile] = Listed;
        _server.Files[ParentFile] = new RemoteFile(ParentLength, ParentTime);

        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes()
            .RegisterNode(typeof(ForEachNode))
            .RegisterNode(typeof(SftpDeleteNode));
        services.AddSingleton(_etlContext);
        services.AddSingleton(_sessionFactory);

        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(JsonDocument.Parse(new JsonObject
        {
            ["current"] = new JsonObject
            {
                ["fullPath"] = ParentFile,
                ["length"] = ParentLength,
                ["lastWriteTimeUtc"] = "2020-02-03T04:05:06.0000000Z"
            }
        }.ToJsonString()));
        _disposables.Add(dataContext);
        var root = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext, null, null,
            null);

        await ListAsync(dataContext, root,
            new SftpEntry("AR00001.TXT", RemoteFile, false, first.Length, first.LastWriteTimeUtc),
            new SftpEntry("AR00002.TXT", SecondFile, false, ListedLength, ListedTime));
        _server.Files[SecondFile] = second;

        var next = A.Fake<NodeDelegate>();
        var loop = await LoadLoopAsync(mismatchLine);
        var loopContext = root.RegisterChildNode("ForEach", 1, loop, dataContext);
        try
        {
            await new ForEachNode(next).ProcessObjectAsync(dataContext, loopContext);
            return (null, next, logger);
        }
        catch (Exception e)
        {
            return (e, next, logger);
        }
    }

    public static TheoryData<long, long> Replacements => new()
    {
        { ListedLength + 1, 0 },
        { ListedLength, TimeSpan.TicksPerSecond }
    };

    [Theory]
    [MemberData(nameof(Replacements))]
    public async Task InsideAForEach_Warn_DeletesTheUnchangedFile_AndKeepsTheReplacedOne(long length, long tickShift)
    {
        var replacement = new RemoteFile(length, ListedTime.AddTicks(tickShift), "delivered again");

        var (error, next, logger) = await ListAndLoopAsync("onIdentityMismatch: Warn", replacement);

        Assert.Null(error);
        // The first file left through its temporary name, the replaced one was not moved, and the
        // file the parent context names was never addressed.
        Assert.Equal(new Dictionary<string, RemoteFile>
        {
            [SecondFile] = replacement,
            [ParentFile] = new RemoteFile(ParentLength, ParentTime)
        }, _server.Files);
        Assert.Contains($"Delete {ParkedPath()}", _server.Operations);
        Assert.DoesNotContain(_server.Operations, o => o.Contains("PARENT", StringComparison.Ordinal));
        Assert.Contains($"GetEntry {SecondFile}", _server.Operations);
        Assert.DoesNotContain(_server.Operations, o => o.StartsWith($"Move {SecondFile}", StringComparison.Ordinal));
        // The warning quotes the identity of the second element: its own length and time, not the
        // first element's and not the parent's.
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
                A<string>.That.Contains("is not the file that was listed"),
                A<object[]>.That.Matches(a => a.SequenceEqual(new object[]
                    { SecondFile, ListedIdentity, Identity(length, ListedTime.AddTicks(tickShift)) }))))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [MemberData(nameof(Replacements))]
    public async Task InsideAForEach_Fail_DeletesTheUnchangedFile_KeepsTheReplacedOne_AndFails(long length,
        long tickShift)
    {
        var replacement = new RemoteFile(length, ListedTime.AddTicks(tickShift), "delivered again");

        var (error, next, _) = await ListAndLoopAsync("", replacement);

        var failure = Assert.IsType<MeshAdapterPipelineExecutionException>(error);
        Assert.Contains(
            $"Remote file '{SecondFile}' is not the file that was listed: expected {ListedIdentity}, found {Identity(length, ListedTime.AddTicks(tickShift))}.",
            failure.Message, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, RemoteFile>
        {
            [SecondFile] = replacement,
            [ParentFile] = new RemoteFile(ParentLength, ParentTime)
        }, _server.Files);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task InsideAForEach_UnchangedFiles_AreAllDeleted_AndTheChainContinues()
    {
        var (error, next, logger) = await ListAndLoopAsync("", Listed);

        Assert.Null(error);
        Assert.Equal(new Dictionary<string, RemoteFile> { [ParentFile] = new RemoteFile(ParentLength, ParentTime) },
            _server.Files);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._)).MustNotHaveHappened();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustHaveHappenedOnceExactly();
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Deletes one file from an SFTP server: it removes the file <c>SftpDownload@1</c> read.
/// Meant to run inside a <c>ForEach@1</c> over an <c>SftpList@1</c> result, one session per
/// file.
/// <para />
/// A file that is not there any more is not automatically a failure: the goal state is that it
/// is gone. Which of the two a run should show is stated per pipeline through
/// <c>onMissingFile</c>, and the strict reading is the default. The tolerance covers what
/// the server reports as a missing path; a server that answers a doomed delete with a generic
/// failure instead is not distinguishable here and surfaces as a delete failure.
/// <para />
/// With an expected identity - the length and last write time the listing reported (AB#8284) -
/// the node deletes the file only while it is still the file that was listed. Plain SFTP has no
/// conditional delete, so the node reads the identity under the listed name first and leaves a
/// different file alone; only a matching file is moved to a name nobody else uses
/// (<see cref="SftpParkedName.PathFor" />), checked again there and deleted there. Nothing is ever renamed
/// back: a file that does not match after the move stays under the temporary name and the node
/// fails, so no file under the listed name can be replaced, whatever the server's rename does.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
/// <param name="sessionFactory">Opens the SFTP session, including the concurrency limit and host key check</param>
[NodeConfiguration(typeof(SftpDeleteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class SftpDeleteNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpDeleteNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.RemotePath) && string.IsNullOrWhiteSpace(c.RemotePathPath))
        {
            throw MeshAdapterPipelineExecutionException.NoRemotePathSpecified(nodeContext);
        }

        var remotePath = string.IsNullOrWhiteSpace(c.RemotePathPath)
            ? c.RemotePath
            : dataContext.Get<string>(c.RemotePathPath);

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            // Only reachable with RemotePathPath configured: the guard above already refused a
            // definition where both sources are blank, so the path named here is the one that
            // resolved to nothing.
            throw PipelineExecutionException.ValueNotSet(nodeContext, c.RemotePathPath!);
        }

        if (remotePath.EndsWith('/'))
        {
            // A directory is not a target for this node. Refused here rather than at the
            // server, whose answer says nothing about the actual mistake: which status a
            // doomed delete comes back with depends on the implementation, and the ones in
            // use read the same as a missing file or a plain refusal.
            throw MeshAdapterPipelineExecutionException.RemotePathIsDirectory(nodeContext, remotePath);
        }

        // Resolved before the dry-run branch, so a dry run reports a configuration that a live
        // run would refuse.
        var expected = ResolveExpectedIdentity(c, dataContext, nodeContext);

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            if (expected is { } identity)
            {
                nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SftpDelete, new
                {
                    host = settings.Host,
                    port = settings.Port,
                    username = settings.Username,
                    remotePath,
                    onMissingFile = c.OnMissingFile.ToString(),
                    expectedLength = identity.Length,
                    expectedLastWriteTimeUtc = FormatTime(identity.LastWriteTimeUtc),
                    onIdentityMismatch = c.OnIdentityMismatch.ToString()
                });
            }
            else
            {
                nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SftpDelete, new
                {
                    host = settings.Host,
                    port = settings.Port,
                    username = settings.Username,
                    remotePath,
                    onMissingFile = c.OnMissingFile.ToString()
                });
            }
        }
        else
        {
            Outcome outcome;
            try
            {
                // The session holds the server's concurrency slot until it is disposed, so it
                // stays in a using scope.
                using var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration, etlContext,
                    nodeContext);
                outcome = expected is { } identity
                    ? DeleteIfStillListed(session, remotePath, identity, nodeContext)
                    : new Outcome(session.Delete(remotePath) ? DeleteResult.Deleted : DeleteResult.Missing);
            }
            catch (MeshAdapterPipelineExecutionException)
            {
                // A host key mismatch or an exhausted slot wait already names this node and the
                // server configuration. Wrapping it again would bury that under a transport
                // message.
                throw;
            }
            catch (Exception e)
            {
                // Name the node, the way the sibling upload node does. A bare SSH.NET message
                // leaves whoever reads the run guessing which step it came from.
                throw MeshAdapterPipelineExecutionException.CannotDeleteViaSftp(nodeContext, remotePath, e);
            }

            // Outside the try: a file that was already gone is not a transport failure and
            // must not be reported as one, and Dispose has released the connection slot
            // before the decision is taken.
            if (outcome.Result == DeleteResult.Deleted)
            {
                if (expected is null)
                {
                    nodeContext.Info("SftpDelete: removed '{0}'", remotePath);
                }
                else
                {
                    nodeContext.Info("SftpDelete: removed '{0}', it was still the file that was listed", remotePath);
                }
            }
            else if (outcome.Result == DeleteResult.Changed)
            {
                // Never moved, still under its name, so the next listing offers it again; whether that is an
                // error is the pipeline's call.
                if (c.OnIdentityMismatch == IdentityMismatchHandling.Warn)
                {
                    nodeContext.Warning(
                        "SftpDelete: '{0}' is not the file that was listed (expected {1}, found {2}); it stays under its name and was not deleted",
                        remotePath, outcome.Expected, outcome.Found);
                }
                else
                {
                    throw MeshAdapterPipelineExecutionException.SftpFileIdentityChanged(nodeContext, remotePath,
                        outcome.Expected, outcome.Found);
                }
            }
            else if (c.OnMissingFile == MissingFileHandling.Ignore)
            {
                nodeContext.Warning("SftpDelete: '{0}' does not exist any more, nothing to delete", remotePath);
            }
            else
            {
                throw MeshAdapterPipelineExecutionException.SftpFileNotFound(nodeContext, remotePath);
            }
        }

        // Outside the try, and reached from both branches: the catch above speaks for the
        // deletion, so a failure further down the chain must not come back as "Cannot delete
        // file via SFTP". A dry run took the other branch and removed nothing, but the chain
        // still has to see the run through.
        await next(dataContext, nodeContext);
    }

    // Zero to seven fractional digits: for parsing, F makes the digits optional and the point with them.
    private const string ListedTimeFormatWithoutZone = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    private enum DeleteResult
    {
        Deleted,
        Missing,
        Changed
    }

    /// <param name="Result">What happened to the file</param>
    /// <param name="Expected">For <see cref="DeleteResult.Changed" />: the identity that was expected</param>
    /// <param name="Found">For <see cref="DeleteResult.Changed" />: what was found instead</param>
    private sealed record Outcome(DeleteResult Result, string Expected = "", string Found = "");

    private readonly record struct ExpectedIdentity(long Length, DateTime LastWriteTimeUtc);

    /// <summary>
    /// Deletes the file only while it still has the expected identity:
    /// <list type="number">
    /// <item><description>Read the identity under the listed name. Missing: a missing file. Different, or a
    /// directory: nothing is moved, the file stays under its name.</description></item>
    /// <item><description>Move the file to a name nobody else uses (<see cref="SftpParkedName.PathFor" />), read
    /// the identity again there and delete it there. A file delivered again under the listed name after the move
    /// is never touched.</description></item>
    /// </list>
    /// Nothing is renamed back. A file that does not have the expected identity after the move and
    /// every failure after the move leave the file under the temporary name and fail the node with
    /// both names, whatever <c>OnIdentityMismatch</c> says: <c>SftpList@1</c> skips the temporary
    /// name unless its pattern asks for it, so such a file needs a person.
    /// </summary>
    private static Outcome DeleteIfStillListed(ISftpSession session, string remotePath, ExpectedIdentity expected,
        INodeContext nodeContext)
    {
        // A failure here leaves everything where it was, so the node's general failure fits.
        var current = session.GetEntry(remotePath);
        if (current is null)
        {
            return new Outcome(DeleteResult.Missing);
        }

        if (!HasIdentity(current, expected))
        {
            return new Outcome(DeleteResult.Changed, Describe(expected), Describe(current));
        }

        var parkedPath = SftpParkedName.PathFor(remotePath);
        bool moved;
        try
        {
            moved = session.MoveToNewName(remotePath, parkedPath);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.CannotMoveAsideViaSftp(nodeContext, remotePath, parkedPath,
                Describe(expected), e);
        }

        if (!moved)
        {
            return new Outcome(DeleteResult.Missing);
        }

        SftpEntry? found;
        try
        {
            found = session.GetEntry(parkedPath);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.SftpMovedAsideFileUnchecked(nodeContext, remotePath,
                parkedPath, Describe(expected), e);
        }

        // Gone from a name nobody else uses: someone removed it in the meantime, which is a
        // missing file like any other.
        if (found is null)
        {
            return new Outcome(DeleteResult.Missing);
        }

        if (!HasIdentity(found, expected))
        {
            throw MeshAdapterPipelineExecutionException.SftpReplacedFileLeftAside(nodeContext, remotePath, parkedPath,
                Describe(expected), Describe(found));
        }

        bool deleted;
        try
        {
            deleted = session.Delete(parkedPath);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.CannotDeleteMovedAsideViaSftp(nodeContext, remotePath,
                parkedPath, e);
        }

        return new Outcome(deleted ? DeleteResult.Deleted : DeleteResult.Missing);
    }

    private static bool HasIdentity(SftpEntry entry, ExpectedIdentity expected)
    {
        return !entry.IsDirectory && entry.Length == expected.Length &&
               entry.LastWriteTimeUtc.Ticks == expected.LastWriteTimeUtc.Ticks;
    }

    /// <summary>
    /// Reads the expected identity, or null when none is configured. Length and last write time
    /// go together: half an identity is refused rather than half checked, a value that cannot be
    /// compared is refused rather than skipped, and <c>Warn</c> without an identity is refused
    /// rather than ignored.
    /// </summary>
    private static ExpectedIdentity? ResolveExpectedIdentity(SftpDeleteNodeConfiguration c, IDataContext dataContext,
        INodeContext nodeContext)
    {
        // A path that is set counts as set, blank or not: a blank one is refused below rather than
        // read as "no check", which would delete without one.
        var lengthConfigured = c.ExpectedLength is not null || c.ExpectedLengthPath is not null;
        var timeConfigured = c.ExpectedLastWriteTimeUtc is not null || c.ExpectedLastWriteTimeUtcPath is not null;

        if (!lengthConfigured && !timeConfigured)
        {
            // Fail cannot be told from the default, so only Warn shows a definition that expects a
            // check here. Ignoring it would delete the file unchecked.
            if (c.OnIdentityMismatch == IdentityMismatchHandling.Warn)
            {
                throw MeshAdapterPipelineExecutionException.SftpDeleteWarnWithoutIdentity(nodeContext);
            }

            return null;
        }

        if (!lengthConfigured)
        {
            throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityIncomplete(nodeContext,
                nameof(c.ExpectedLength));
        }

        if (!timeConfigured)
        {
            throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityIncomplete(nodeContext,
                nameof(c.ExpectedLastWriteTimeUtc));
        }

        return new ExpectedIdentity(ResolveExpectedLength(c, dataContext, nodeContext),
            ResolveExpectedLastWriteTime(c, dataContext, nodeContext));
    }

    private static long ResolveExpectedLength(SftpDeleteNodeConfiguration c, IDataContext dataContext,
        INodeContext nodeContext)
    {
        const string expectation = "a length of zero or more bytes, as a JSON integer or a string of digits";

        // A configured path decides alone, as RemotePathPath does: falling back to the static
        // value would check against a length the pipeline did not hand over in this iteration.
        if (c.ExpectedLengthPath is not null)
        {
            if (string.IsNullOrWhiteSpace(c.ExpectedLengthPath))
            {
                throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityPathBlank(nodeContext,
                    nameof(c.ExpectedLengthPath));
            }

            var value = dataContext.GetValue(c.ExpectedLengthPath, false);
            // Integers box to int or long. A real is refused rather than rounded, and a string
            // only counts when it is digits and nothing else.
            long? length = value switch
            {
                int number => number,
                long number => number,
                string text when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => null
            };

            if (length is not >= 0)
            {
                throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityValueInvalid(nodeContext,
                    $"{nameof(c.ExpectedLengthPath)} '{c.ExpectedLengthPath}'", value, expectation);
            }

            return length.Value;
        }

        if (c.ExpectedLength is not >= 0)
        {
            throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityValueInvalid(nodeContext,
                nameof(c.ExpectedLength), c.ExpectedLength, expectation);
        }

        return c.ExpectedLength.Value;
    }

    private static DateTime ResolveExpectedLastWriteTime(SftpDeleteNodeConfiguration c, IDataContext dataContext,
        INodeContext nodeContext)
    {
        string source;
        object? value;
        if (c.ExpectedLastWriteTimeUtcPath is not null)
        {
            if (string.IsNullOrWhiteSpace(c.ExpectedLastWriteTimeUtcPath))
            {
                throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityPathBlank(nodeContext,
                    nameof(c.ExpectedLastWriteTimeUtcPath));
            }

            source = $"{nameof(c.ExpectedLastWriteTimeUtcPath)} '{c.ExpectedLastWriteTimeUtcPath}'";
            // Read as text: with date parsing on, the data context hands over a DateTime that has
            // already been through a parser this node does not control.
            value = dataContext.GetValue(c.ExpectedLastWriteTimeUtcPath, false);
        }
        else
        {
            source = nameof(c.ExpectedLastWriteTimeUtc);
            value = c.ExpectedLastWriteTimeUtc;
        }

        if (value is string text && TryParseListedTime(text, out var time))
        {
            // SSH.NET reports DateTime.MinValue for a server that sends no modification time, and
            // SftpList@1 writes it for every file; every file would then match on the time.
            if (time == DateTime.MinValue)
            {
                throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityTimeNotReported(nodeContext, source,
                    text);
            }

            return time;
        }

        throw MeshAdapterPipelineExecutionException.SftpDeleteIdentityValueInvalid(nodeContext, source, value,
            "the UTC time as SftpList@1 writes it: yyyy-MM-ddTHH:mm:ss, up to seven fractional digits and a closing Z");
    }

    /// <summary>
    /// Reads a time in the form <c>SftpList@1</c> writes, with zero to seven fractional digits.
    /// The shape decides what is accepted - ASCII digits, an upper-case <c>T</c> and a closing
    /// upper-case <c>Z</c>, nothing before or after - and the parse after it checks the calendar.
    /// The result carries the digits as written, so it compares tick for tick with the time a
    /// listing reports, whatever the time zone of the pod.
    /// </summary>
    private static bool TryParseListedTime(string text, out DateTime time)
    {
        time = default;
        return ListedTimeShape().IsMatch(text) &&
               DateTime.TryParseExact(text[..^1], ListedTimeFormatWithoutZone, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out time);
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ListedTimeShape();

    private static string FormatTime(DateTime time)
    {
        // The format SftpList@1 writes, so the two can be compared by eye.
        return time.ToString(SftpListNode.LastWriteTimeFormat, CultureInfo.InvariantCulture);
    }

    private static string Describe(ExpectedIdentity identity)
    {
        return $"length {identity.Length}, last write time {FormatTime(identity.LastWriteTimeUtc)}";
    }

    private static string Describe(SftpEntry entry)
    {
        return entry.IsDirectory
            ? "a directory"
            : $"length {entry.Length}, last write time {FormatTime(entry.LastWriteTimeUtc)}";
    }
}

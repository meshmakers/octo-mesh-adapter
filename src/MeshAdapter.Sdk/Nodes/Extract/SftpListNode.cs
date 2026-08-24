using System.Globalization;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Lists an SFTP directory and writes one element per matching file to the target path.
/// Metadata only: the array is meant to be iterated with <c>ForEach@1</c>, reading each file
/// with <c>SftpDownload@1</c>. Splitting listing from reading lets a consumer drop files it has
/// already processed before anything is transferred.
/// <para />
/// A server that omits modification times reports the same value for every entry, so a
/// consumer that derives a file identity from <c>lastWriteTimeUtc</c> would see them all as
/// one file. Every SFTP server in practice reports it; a listing where it is missing is worth
/// treating as a misconfigured server rather than as data.
/// <para />
/// The array is always written, even when nothing matches, because a downstream
/// <c>ForEach@1</c> aborts with <c>PathMustBeArray</c> when its iteration path holds no array.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
/// <param name="sessionFactory">Opens the SFTP session, including the concurrency limit and host key check</param>
[NodeConfiguration(typeof(SftpListNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpListNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpListNodeConfiguration>();

        // 'required' is a C# concept: the pipeline deserializer only rejects unknown
        // properties, so a definition leaving either of these out arrives here as an empty
        // string. Both are guarded, because the enforcement gap is the same for both and the
        // directory would otherwise reach SSH.NET and come back as a raw path error.
        if (string.IsNullOrWhiteSpace(c.RemoteDirectory))
        {
            throw MeshAdapterPipelineExecutionException.RemoteDirectoryNotConfigured(nodeContext);
        }

        if (string.IsNullOrWhiteSpace(c.FilePattern))
        {
            throw MeshAdapterPipelineExecutionException.FilePatternNotConfigured(nodeContext);
        }

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);
        var now = DateTime.UtcNow;
        var glob = SftpFileNameGlob.Compile(c.FilePattern);

        List<SftpEntry> entries;
        try
        {
            using var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration, etlContext,
                nodeContext);
            entries = session.List(c.RemoteDirectory)
                .Where(e => !e.IsDirectory)
                .Where(e => IsPlainName(e, nodeContext))
                .Where(e => glob.IsMatch(e.Name))
                // Only guard when asked to. The server's clock and this pod's clock are
                // independent, so an unconditional comparison would drop a file whose mtime
                // runs slightly ahead - invisibly, until the skew had passed.
                .Where(e => c.MinFileAgeSeconds <= 0 ||
                            (now - e.LastWriteTimeUtc).TotalSeconds >= c.MinFileAgeSeconds)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (MeshAdapterPipelineExecutionException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Name the node, the way the sibling upload node does. A bare SSH.NET message
            // leaves whoever reads the run guessing which step it came from.
            throw MeshAdapterPipelineExecutionException.CannotListViaSftp(nodeContext, e);
        }

        var files = new JsonArray();
        foreach (var entry in entries)
        {
            files.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["fullPath"] = entry.FullPath,
                ["length"] = entry.Length,
                // Spelled out rather than the round-trip specifier, which renders according to
                // the value's Kind: a Local value would carry a daylight-saving-dependent
                // offset and an Unspecified one no zone at all. A consumer derives a file
                // identity from this string, so the same instant must render identically or
                // the identity changes underneath it and nothing counts as processed any more.
                // The value is UTC by contract, which is what the property name states.
                ["lastWriteTimeUtc"] = entry.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
                    CultureInfo.InvariantCulture),
                // Where the element came from, so a consumer can scope its own bookkeeping
                // without repeating these three values in its own configuration. A JsonNode
                // belongs to one parent, so every element builds its own object.
                ["source"] = new JsonObject
                {
                    ["serverConfiguration"] = c.ServerConfiguration,
                    ["remoteDirectory"] = c.RemoteDirectory,
                    ["filePattern"] = c.FilePattern
                }
            });
        }

        dataContext.Set(c.TargetPath, files, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        nodeContext.Debug("SftpList: {0} file(s) in '{1}' match '{2}'", files.Count, c.RemoteDirectory,
            c.FilePattern);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// A listing entry names one member of the directory that was listed. A name carrying a
    /// path separator comes from a misbehaving or hostile server and would steer whatever
    /// reads the emitted path somewhere else entirely, so it is reported and dropped rather
    /// than passed on.
    /// </summary>
    private static bool IsPlainName(SftpEntry entry, INodeContext nodeContext)
    {
        if (!entry.Name.Contains('/') && !entry.Name.Contains('\\'))
        {
            return true;
        }

        nodeContext.Warning("SftpList: skipping listing entry '{0}', a file name cannot contain a path separator",
            entry.Name);
        return false;
    }
}

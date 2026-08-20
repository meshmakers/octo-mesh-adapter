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

        if (string.IsNullOrWhiteSpace(c.FilePattern))
        {
            // 'required' is a C# concept: the pipeline deserializer only rejects unknown
            // properties, so a definition without the pattern arrives here as an empty string.
            throw MeshAdapterPipelineExecutionException.FilePatternNotConfigured(nodeContext);
        }

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);
        var now = DateTime.UtcNow;

        List<SftpEntry> entries;
        using (var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration))
        {
            entries = session.List(c.RemoteDirectory)
                .Where(e => !e.IsDirectory)
                .Where(e => SftpFileNameGlob.Matches(e.Name, c.FilePattern))
                .Where(e => (now - e.LastWriteTimeUtc).TotalSeconds >= c.MinFileAgeSeconds)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        }

        var files = new JsonArray();
        foreach (var entry in entries)
        {
            files.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["fullPath"] = entry.FullPath,
                ["length"] = entry.Length,
                // Round-trip format on purpose: a consumer derives a file identity from this
                // string, so it has to read the same on every listing of an unchanged file.
                ["lastWriteTimeUtc"] = entry.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
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
}

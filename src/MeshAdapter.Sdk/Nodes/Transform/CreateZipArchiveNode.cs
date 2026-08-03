using System.IO.Compression;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Bundles an array of <c>{ fileName, contentBase64 }</c> entries into a single
/// ZIP archive. A <c>fileName</c> may contain forward slashes to create folders
/// inside the archive (e.g. group documents by AP/AR). An entry may instead carry
/// its content as a scratch reference (<c>{ fileName, scratchFileToken, length }</c>,
/// produced e.g. by <c>MergePdf@1</c> in scratch mode) — the node then streams the
/// content straight from the scratch file into the archive.
/// <para>
/// Alternatively an entry may carry <c>pathSegments</c> (array of folder names ending
/// with the file name) instead of <c>fileName</c>; each segment is sanitized so
/// data-derived values (vendor names etc.) cannot create unintended folders. With
/// <see cref="CreateZipArchiveNodeConfiguration.AppendSequenceNumber"/> a running
/// number is inserted before each content entry's extension (unique names), and with
/// <see cref="CreateZipArchiveNodeConfiguration.ManifestFileName"/> a CSV manifest of
/// all content entries (per-entry <c>manifest</c> object + final path) is written as
/// the first archive entry.
/// </para>
/// <para>
/// By default the archive is written back as base64 to the configured
/// <c>TargetPath</c>. With
/// <see cref="CreateZipArchiveNodeConfiguration.PersistAsFileSystemItem"/> the archive
/// is streamed to a scratch file and persisted directly as a FileSystemItem (the item's
/// RtId is written to <c>TargetPath</c>) — this keeps the whole ZIP off the managed heap
/// and avoids OOM on large fiscal-year exports (AB#4642).
/// </para>
/// </summary>
[NodeConfiguration(typeof(CreateZipArchiveNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateZipArchiveNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private static readonly RtCkId<CkTypeId> RtCkTypeIdFileSystemItem =
        new("System.Reporting", "FileSystemItem");

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<CreateZipArchiveNodeConfiguration>();

        if (dataContext.Get<JsonNode>(config.Path) is not JsonArray entries)
        {
            throw MeshAdapterPipelineExecutionException.ZipEntriesInvalid(nodeContext, config.Path);
        }

        if (config.PersistAsFileSystemItem)
        {
            await PersistArchiveAsFileSystemItemAsync(dataContext, nodeContext, config, entries);
        }
        else
        {
            await WriteArchiveAsBase64Async(dataContext, nodeContext, config, entries);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Streams the archive into a scratch file and persists it as a FileSystemItem. The ZIP
    /// never exists as a contiguous byte[]/base64 string in memory.
    /// </summary>
    private async Task PersistArchiveAsFileSystemItemAsync(IDataContext dataContext, INodeContext nodeContext,
        CreateZipArchiveNodeConfiguration config, JsonArray entries)
    {
        if (string.IsNullOrWhiteSpace(config.RootFolderWellKnownName))
        {
            throw MeshAdapterPipelineExecutionException.RootFolderWellKnownNameNotSet(nodeContext);
        }

        var scratchSpace = nodeContext.ScratchSpace
                           ?? throw MeshAdapterPipelineExecutionException.ScratchSpaceRequired(nodeContext,
                               "PersistAsFileSystemItem streams the archive to a scratch file");

        var zipToken = scratchSpace.CreateFile("zip");
        await using (var zipWriteStream = scratchSpace.OpenWrite(zipToken))
        using (var archive = new ZipArchive(zipWriteStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            await WriteEntriesToArchiveAsync(archive, entries, nodeContext, config);
        }

        var zipLength = scratchSpace.GetLength(zipToken);
        var fileName = ResolveFileName(dataContext, config) ?? $"archive-{Guid.NewGuid():N}.zip";

        nodeContext.Debug(
            $"Created ZIP archive with {entries.Count} entries ({zipLength} bytes) -> FileSystemItem '{fileName}'");

        OctoObjectId rtId;
        await using (var zipReadStream = scratchSpace.OpenRead(zipToken))
        {
            rtId = await PersistFileSystemItemAsync(zipReadStream, zipLength, fileName, config);
        }

        dataContext.Set(config.TargetPath, rtId.ToString(),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
    }

    /// <summary>Legacy behaviour: build the archive in memory and write it back as base64.</summary>
    private async Task WriteArchiveAsBase64Async(IDataContext dataContext, INodeContext nodeContext,
        CreateZipArchiveNodeConfiguration config, JsonArray entries)
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntriesToArchiveAsync(archive, entries, nodeContext, config);
        }

        var zipBytes = zipStream.ToArray();
        nodeContext.Debug($"Created ZIP archive with {entries.Count} entries ({zipBytes.Length} bytes)");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(zipBytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        if (!string.IsNullOrEmpty(config.ContentLengthTargetPath))
        {
            dataContext.Set(config.ContentLengthTargetPath, (long)zipBytes.Length,
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
        }
    }

    /// <summary>
    /// Writes every entry into the archive. Entry names are resolved up front (sanitized
    /// path segments, optional running number) so an optional CSV manifest referencing the
    /// FINAL names can be written as the first archive entry. Each entry's content comes
    /// either from a scratch file (<c>scratchFileToken</c>, streamed) or from an inline
    /// <c>contentBase64</c> string. Consumed entries are detached from the input array so
    /// their (possibly large) content is eligible for GC before the whole archive is finished.
    /// </summary>
    private static async Task WriteEntriesToArchiveAsync(ZipArchive archive, JsonArray entries,
        INodeContext nodeContext, CreateZipArchiveNodeConfiguration config)
    {
        var resolvedNames = ResolveEntryNames(entries, nodeContext, config);

        if (!string.IsNullOrWhiteSpace(config.ManifestFileName))
        {
            await WriteManifestAsync(archive, entries, resolvedNames, config);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            // Validated in ResolveEntryNames; the cast cannot fail here.
            var entry = (JsonObject)entries[i]!;
            var fileName = resolvedNames[i];

            var zipEntry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            await using var entryStream = zipEntry.Open();

            var scratchToken = AsString(Prop(entry, "scratchFileToken"));
            if (!string.IsNullOrEmpty(scratchToken))
            {
                var scratchSpace = nodeContext.ScratchSpace
                                   ?? throw MeshAdapterPipelineExecutionException.ScratchSpaceRequired(nodeContext,
                                       $"entry '{fileName}' references a scratch file");
                await using var contentStream = scratchSpace.OpenRead(scratchToken);
                await contentStream.CopyToAsync(entryStream);
            }
            else
            {
                var contentBase64 = AsString(Prop(entry, "contentBase64"));
                if (string.IsNullOrEmpty(contentBase64))
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i,
                        $"neither 'scratchFileToken' nor 'contentBase64' is set for '{fileName}'");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(contentBase64);
                }
                catch (FormatException)
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i,
                        $"'contentBase64' is not valid base64 for '{fileName}'");
                }

                await entryStream.WriteAsync(bytes);
            }

            // Release the entry (and its inline content, if any) as we go.
            entries[i] = null;
        }
    }

    private async Task<OctoObjectId> PersistFileSystemItemAsync(Stream content, long length, string fileName,
        CreateZipArchiveNodeConfiguration config)
    {
        var folder = await GetFolderRootAsync(etlContext.TenantRepository, config.RootFolderWellKnownName!);

        var rtFileSystemItem =
            await etlContext.TenantRepository.CreateTransientRtEntityByRtCkIdAsync(RtCkTypeIdFileSystemItem);
        if (config.GenerateRtId)
        {
            rtFileSystemItem.RtId = OctoObjectId.GenerateNewId();
        }

        var entityBinaryInfo = new EntityBinaryInfo
        {
            ContentType = config.ContentType,
            Filename = fileName,
            Size = length,
            Stream = content
        };
        rtFileSystemItem.SetAttributeValue("Content", AttributeValueTypesDto.BinaryLinked, entityBinaryInfo);
        rtFileSystemItem.SetAttributeValue("Name", AttributeValueTypesDto.String, fileName);

        var entityUpdateInfoList = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(RtCkTypeIdFileSystemItem, rtFileSystemItem)
        };
        var assocUpdateInfoList = new List<AssociationUpdateInfo>
        {
            AssociationUpdateInfo.CreateInsert(rtFileSystemItem.ToRtEntityId(), folder.ToRtEntityId(),
                SystemCkIds.RtCkParentChildRoleId)
        };

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await etlContext.TenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, assocUpdateInfoList,
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw MeshAdapterPipelineExecutionException.RepositoryUpdateOperationFailed(operationResult);
        }

        await session.CommitTransactionAsync();
        return rtFileSystemItem.RtId;
    }

    private static async Task<RtEntity> GetFolderRootAsync(ITenantRepository tenantRepository,
        string rootFolderWellKnownName)
    {
        try
        {
            var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldEquals(nameof(RtEntity.RtWellKnownName), rootFolderWellKnownName);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync(session, "System.Reporting/FolderRoot",
                queryOptions);

            await session.CommitTransactionAsync();
            if (r.Items.Count() == 1)
            {
                return r.Items.First();
            }

            throw MeshAdapterPipelineExecutionException.RootFolderNotFound(rootFolderWellKnownName);
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.RepositoryOperationFailed(ex);
        }
    }

    private static string? ResolveFileName(IDataContext dataContext, CreateZipArchiveNodeConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.FileName))
        {
            return config.FileName;
        }

        return string.IsNullOrWhiteSpace(config.FileNamePath) ? null : dataContext.Get<string>(config.FileNamePath);
    }

    /// <summary>
    /// Resolves the final archive path of every entry: <c>pathSegments</c> (sanitized per
    /// segment, joined with '/') wins over a pre-composed <c>fileName</c>; with
    /// <see cref="CreateZipArchiveNodeConfiguration.AppendSequenceNumber"/> a running number
    /// is inserted before the extension so names are guaranteed unique.
    /// </summary>
    private static List<string> ResolveEntryNames(JsonArray entries, INodeContext nodeContext,
        CreateZipArchiveNodeConfiguration config)
    {
        var names = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject entry)
            {
                throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i, "not a JSON object");
            }

            string fileName;
            if (Prop(entry, "pathSegments") is JsonArray segments)
            {
                var sanitized = segments.Select(s => SanitizeSegment(AsString(s)))
                    .Where(s => s.Length > 0)
                    .ToList();
                if (sanitized.Count == 0)
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i,
                        "'pathSegments' resolves to an empty path");
                }

                fileName = string.Join('/', sanitized);
            }
            else
            {
                fileName = NormalizeEntryName(AsString(Prop(entry, "fileName")));
                if (string.IsNullOrEmpty(fileName))
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i,
                        "'fileName' is empty");
                }
            }

            if (config.AppendSequenceNumber)
            {
                fileName = InsertSequenceNumber(fileName, i + 1);
            }

            names.Add(fileName);
        }

        return names;
    }

    /// <summary>
    /// Writes the CSV manifest (one row per content entry: the entry's <c>manifest</c>
    /// object fields plus its final archive path) as an archive entry. Semicolon-separated
    /// (configurable), UTF-8 with BOM, CRLF — import- and Excel-friendly.
    /// </summary>
    private static async Task WriteManifestAsync(ZipArchive archive, JsonArray entries,
        List<string> resolvedNames, CreateZipArchiveNodeConfiguration config)
    {
        var delimiter = string.IsNullOrEmpty(config.ManifestDelimiter) ? ";" : config.ManifestDelimiter;

        // Column set = file-name column + ordered union of all manifest object keys.
        var columns = new List<string>();
        foreach (var entryNode in entries)
        {
            if (entryNode is JsonObject entry && Prop(entry, "manifest") is JsonObject manifest)
            {
                foreach (var pair in manifest)
                {
                    if (!columns.Contains(pair.Key))
                    {
                        columns.Add(pair.Key);
                    }
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        AppendCsvRow(sb, new[] { config.ManifestFileNameColumn }.Concat(columns), delimiter);
        for (var i = 0; i < entries.Count; i++)
        {
            var manifest = entries[i] is JsonObject entry ? Prop(entry, "manifest") as JsonObject : null;
            var values = new List<string> { resolvedNames[i] };
            values.AddRange(columns.Select(c => manifest is null ? string.Empty : AsString(Prop(manifest, c))));
            AppendCsvRow(sb, values, delimiter);
        }

        var manifestEntry = archive.CreateEntry(SanitizeSegment(config.ManifestFileName!), CompressionLevel.Optimal);
        await using var stream = manifestEntry.Open();
        // UTF-8 BOM so Excel and BMD detect the encoding.
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
        await writer.WriteAsync(sb.ToString());
    }

    private static void AppendCsvRow(System.Text.StringBuilder sb, IEnumerable<string> values, string delimiter)
    {
        sb.AppendJoin(delimiter, values.Select(v => CsvQuote(v, delimiter)));
        sb.Append("\r\n");
    }

    private static string CsvQuote(string value, string delimiter)
    {
        if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    /// <summary>
    /// Inserts a zero-padded running number before the extension of the path's last
    /// segment (<c>AP/x.pdf</c> → <c>AP/x_001.pdf</c>; no extension → appended at the end).
    /// </summary>
    private static string InsertSequenceNumber(string fileName, int sequence)
    {
        var suffix = $"_{sequence:D3}";
        var dot = fileName.LastIndexOf('.');
        var lastSlash = fileName.LastIndexOf('/');
        return dot > lastSlash + 1
            ? fileName[..dot] + suffix + fileName[dot..]
            : fileName + suffix;
    }

    /// <summary>
    /// Sanitizes a single path segment for file-system use: path separators, characters
    /// invalid on Windows and control characters become '_'; surrounding whitespace and
    /// trailing dots are trimmed; overlong segments are capped so extracted paths stay
    /// within file-system limits.
    /// </summary>
    private static string SanitizeSegment(string segment)
    {
        var chars = segment.Select(c => c is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*' || c < ' '
            ? '_'
            : c).ToArray();
        var sanitized = new string(chars).Trim().TrimEnd('.');
        if (sanitized.Length > 100)
        {
            // Preserve a short extension when capping (keeps e.g. '.pdf' openable).
            var dot = sanitized.LastIndexOf('.');
            var extension = dot > 0 && sanitized.Length - dot <= 10 ? sanitized[dot..] : string.Empty;
            sanitized = sanitized[..(100 - extension.Length)].TrimEnd('.', ' ') + extension;
        }

        return sanitized;
    }

    /// <summary>
    /// Normalizes an archive entry path: backslashes become forward slashes and
    /// any leading slashes are trimmed so entries are always relative.
    /// </summary>
    private static string NormalizeEntryName(string fileName)
    {
        return fileName.Replace('\\', '/').TrimStart('/').Trim();
    }

    private static JsonNode? Prop(JsonObject obj, string name)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string AsString(JsonNode? node)
    {
        return node?.ToString() ?? string.Empty;
    }
}

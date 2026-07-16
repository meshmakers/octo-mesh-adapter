using System.IO.Compression;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Bundles an array of <c>{ fileName, contentBase64 }</c> entries into a single
/// ZIP archive written back as base64. A <c>fileName</c> may contain forward
/// slashes to create folders inside the archive (e.g. group documents by AP/AR).
/// See <see cref="CreateZipArchiveNodeConfiguration"/> for the entry shape.
/// </summary>
[NodeConfiguration(typeof(CreateZipArchiveNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateZipArchiveNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<CreateZipArchiveNodeConfiguration>();

        if (dataContext.Get<JsonNode>(config.Path) is not JsonArray entries)
        {
            throw MeshAdapterPipelineExecutionException.ZipEntriesInvalid(nodeContext, config.Path);
        }

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JsonObject entry)
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i, "not a JSON object");
                }

                var fileName = NormalizeEntryName(AsString(Prop(entry, "fileName")));
                if (string.IsNullOrEmpty(fileName))
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i, "'fileName' is empty");
                }

                var contentBase64 = AsString(Prop(entry, "contentBase64"));
                if (string.IsNullOrEmpty(contentBase64))
                {
                    throw MeshAdapterPipelineExecutionException.ZipEntryInvalid(nodeContext, i,
                        $"'contentBase64' is empty for '{fileName}'");
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

                var zipEntry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
                await using var entryStream = zipEntry.Open();
                await entryStream.WriteAsync(bytes);
            }
        }

        var zipBytes = zipStream.ToArray();
        nodeContext.Debug($"Created ZIP archive with {entries.Count} entries ({zipBytes.Length} bytes)");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(zipBytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
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

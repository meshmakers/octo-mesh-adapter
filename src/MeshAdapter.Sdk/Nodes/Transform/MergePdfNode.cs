using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Merges an ordered array of base64-encoded PDFs into a single PDF (page
/// concatenation) written back as base64. Used to prepend a generated cover
/// sheet to an original document. PDFs that cannot be imported (encrypted,
/// corrupt or unsupported) are skipped with a warning unless
/// <see cref="MergePdfNodeConfiguration.FailOnInvalidPdf"/> is set.
/// </summary>
[NodeConfiguration(typeof(MergePdfNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class MergePdfNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<MergePdfNodeConfiguration>();

        var base64Pdfs = dataContext.GetArray<string>(config.Path)?.ToList();
        if (base64Pdfs == null || base64Pdfs.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.PdfMergeInputEmpty(nodeContext, config.Path);
        }

        using var output = new PdfDocument();
        var imported = 0;

        for (var i = 0; i < base64Pdfs.Count; i++)
        {
            var base64 = base64Pdfs[i];
            if (string.IsNullOrWhiteSpace(base64))
            {
                nodeContext.Warning($"PDF at index {i} is empty and was skipped.");
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(base64);
                using var inputStream = new MemoryStream(bytes);
                using var input = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);
                foreach (var page in input.Pages)
                {
                    output.AddPage(page);
                }

                imported++;
            }
            catch (Exception ex)
            {
                if (config.FailOnInvalidPdf)
                {
                    throw MeshAdapterPipelineExecutionException.PdfMergeItemInvalid(nodeContext, i, ex);
                }

                nodeContext.Warning($"PDF at index {i} could not be imported and was skipped: {ex.Message}");
            }
        }

        if (imported == 0)
        {
            throw MeshAdapterPipelineExecutionException.PdfMergeProducedNothing(nodeContext);
        }

        // Read the page count before Save(): PdfSharp protects the document
        // against further access once it has been serialised.
        var pageCount = output.PageCount;

        using var outStream = new MemoryStream();
        output.Save(outStream);
        var outBytes = outStream.ToArray();

        nodeContext.Debug(
            $"Merged {imported}/{base64Pdfs.Count} PDFs into {pageCount} pages ({outBytes.Length} bytes)");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(outBytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }
}

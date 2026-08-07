using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Assembles an output PDF from an ordered list of page operations over one or more
/// source PDFs. Each op selects a page from a source (by index) and optionally rotates
/// (90° steps) and crops it; the op order is the output page order and pages not
/// referenced are dropped. One contract covers rotate, crop, reorder, delete,
/// split-select and cross-source merge — the server side of the document page editor
/// (AB#4760). base64-in / base64-out, mirroring <see cref="MergePdfNode"/> including its
/// scratch-file large-payload handling.
/// </summary>
[NodeConfiguration(typeof(TransformPdfNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class TransformPdfNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<TransformPdfNodeConfiguration>();

        var base64Sources = dataContext.GetArray<string>(config.Path)?.ToList();
        if (base64Sources == null || base64Sources.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.PdfTransformSourcesEmpty(nodeContext, config.Path);
        }

        var ops = dataContext.GetArray<PdfPageOp>(config.OpsPath)?.Where(o => o != null).Cast<PdfPageOp>().ToList();
        if (ops == null || ops.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.PdfTransformOpsEmpty(nodeContext, config.OpsPath);
        }

        // Import each referenced source once (a null entry marks an unreadable source that
        // was skipped). Dispose them only after Save(), because AddPage keeps a reference
        // into the source document until the output is serialised.
        var loaded = new Dictionary<int, PdfDocument?>();
        try
        {
            using var output = new PdfDocument();
            var producedPages = 0;

            for (var opIndex = 0; opIndex < ops.Count; opIndex++)
            {
                var op = ops[opIndex];

                if (op.SourceIndex < 0 || op.SourceIndex >= base64Sources.Count)
                {
                    throw MeshAdapterPipelineExecutionException.PdfTransformSourceIndexOutOfRange(
                        nodeContext, opIndex, op.SourceIndex, base64Sources.Count);
                }

                var rotation = NormalizeRotation(op.Rotate);
                if (rotation < 0)
                {
                    throw MeshAdapterPipelineExecutionException.PdfTransformInvalidRotation(
                        nodeContext, opIndex, op.Rotate);
                }

                var source = GetSource(loaded, base64Sources, op.SourceIndex, config.FailOnInvalidPdf, nodeContext);
                if (source == null)
                {
                    nodeContext.Warning(
                        $"Op {opIndex} references source {op.SourceIndex} which could not be imported and was skipped.");
                    continue;
                }

                if (op.PageIndex < 0 || op.PageIndex >= source.PageCount)
                {
                    throw MeshAdapterPipelineExecutionException.PdfTransformPageIndexOutOfRange(
                        nodeContext, opIndex, op.SourceIndex, op.PageIndex, source.PageCount);
                }

                var newPage = output.AddPage(source.Pages[op.PageIndex]);

                // Display orientation the crop was drawn in = existing rotation + requested rotation.
                var totalRotation = NormalizeRotation(newPage.Rotate + rotation);

                if (op.Crop is { } crop && crop.Width > 0 && crop.Height > 0)
                {
                    newPage.CropBox = PdfCropGeometry.DisplayRectToCropBox(newPage.MediaBox, totalRotation, crop);
                }

                newPage.Rotate = totalRotation;
                producedPages++;
            }

            if (producedPages == 0)
            {
                throw MeshAdapterPipelineExecutionException.PdfTransformProducedNothing(nodeContext);
            }

            // Read the page count before Save(): PdfSharp protects the document against
            // further access once it has been serialised.
            var pageCount = output.PageCount;

            // Scratch mode: stream the PDF straight to a scratch file and hand the downstream
            // node a small reference instead of a base64 string (keeps large PDFs off the LOH).
            if (config.OutputAsScratchFile && nodeContext.ScratchSpace is { } scratchSpace)
            {
                var token = scratchSpace.CreateFile("pdf");
                await using (var scratchStream = scratchSpace.OpenWrite(token))
                {
                    output.Save(scratchStream, false);
                }

                var length = scratchSpace.GetLength(token);
                nodeContext.Debug(
                    $"Transformed {producedPages} page(s) from {loaded.Count} source(s) into {pageCount} pages ({length} bytes) -> scratch file");

                ScratchFileRef.Write(dataContext, config.TargetPath, token, length, contentType: "application/pdf");

                if (!string.IsNullOrEmpty(config.ContentLengthTargetPath))
                {
                    dataContext.Set(config.ContentLengthTargetPath, length,
                        config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
                }

                await next(dataContext, nodeContext);
                return;
            }

            using var outStream = new MemoryStream();
            output.Save(outStream);
            var outBytes = outStream.ToArray();

            nodeContext.Debug(
                $"Transformed {producedPages} page(s) from {loaded.Count} source(s) into {pageCount} pages ({outBytes.Length} bytes)");

            dataContext.Set(config.TargetPath, Convert.ToBase64String(outBytes),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

            if (!string.IsNullOrEmpty(config.ContentLengthTargetPath))
            {
                dataContext.Set(config.ContentLengthTargetPath, (long)outBytes.Length,
                    config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
            }

            await next(dataContext, nodeContext);
        }
        finally
        {
            foreach (var doc in loaded.Values)
            {
                doc?.Dispose();
            }
        }
    }

    /// <summary>
    /// Imports the source at <paramref name="sourceIndex"/> on first use and caches it.
    /// Returns null when the source is unreadable and <paramref name="failOnInvalid"/> is
    /// false (the caller then skips the op); throws when <paramref name="failOnInvalid"/> is true.
    /// </summary>
    private static PdfDocument? GetSource(Dictionary<int, PdfDocument?> loaded, IReadOnlyList<string?> base64Sources,
        int sourceIndex, bool failOnInvalid, INodeContext nodeContext)
    {
        if (loaded.TryGetValue(sourceIndex, out var cached))
        {
            if (cached == null && failOnInvalid)
            {
                throw MeshAdapterPipelineExecutionException.PdfTransformSourceInvalid(nodeContext, sourceIndex, null);
            }

            return cached;
        }

        var base64 = base64Sources[sourceIndex];
        if (string.IsNullOrWhiteSpace(base64))
        {
            if (failOnInvalid)
            {
                throw MeshAdapterPipelineExecutionException.PdfTransformSourceInvalid(nodeContext, sourceIndex, null);
            }

            loaded[sourceIndex] = null;
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var input = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
            loaded[sourceIndex] = input;
            return input;
        }
        catch (Exception ex)
        {
            if (failOnInvalid)
            {
                throw MeshAdapterPipelineExecutionException.PdfTransformSourceInvalid(nodeContext, sourceIndex, ex);
            }

            nodeContext.Warning($"Source PDF at index {sourceIndex} could not be imported: {ex.Message}");
            loaded[sourceIndex] = null;
            return null;
        }
    }

    /// <summary>Normalizes a rotation to one of 0/90/180/270; returns -1 when not a multiple of 90.</summary>
    private static int NormalizeRotation(int degrees)
    {
        var r = ((degrees % 360) + 360) % 360;
        return r % 90 == 0 ? r : -1;
    }
}

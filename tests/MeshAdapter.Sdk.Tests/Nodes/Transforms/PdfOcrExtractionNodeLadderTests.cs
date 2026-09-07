using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Extraction-ladder wiring of <see cref="PdfOcrExtractionNode"/> with a faked
/// <see cref="IPdfTextExtractor"/>: full text-layer documents are used verbatim with tier
/// "TextLayer" (OCR skipped — the fake PDF bytes would make IronOCR throw), text-on-image
/// pages downgrade the tier to "TextLayerFromOcr", and with PreferTextLayer off the
/// extractor is never consulted (pre-existing OCR-only behavior).
/// </summary>
public class PdfOcrExtractionNodeLadderTests
{
    // Passes the node's %PDF- magic-header check without being a real PDF.
    private static readonly byte[] FakePdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7 not a real pdf");

    private static (IDataContext, INodeContext, NodeDelegate) PrepareTest(
        PdfOcrExtractionNodeConfiguration config)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var testData = new JsonObject
        {
            ["file"] = Convert.ToBase64String(FakePdfBytes)
        };
        IDataContext dataContext = new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("PdfOcrExtraction", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task ProcessObjectAsync_AllPagesHaveTextLayer_UsesLayerAndSkipsOcr()
    {
        var config = new PdfOcrExtractionNodeConfiguration
        {
            Path = "$.file",
            TargetPath = "$.text",
            PreferTextLayer = true
        };

        var (dataContext, nodeContext, next) = PrepareTest(config);

        var extractor = A.Fake<IPdfTextExtractor>();
        A.CallTo(() => extractor.Extract(A<byte[]>._, A<int>._))
            .Returns(new PdfTextExtractionResult(
                [
                    new PdfPageText(1, "Seite eins Inhalt", true),
                    new PdfPageText(2, "Seite zwei Inhalt", true)
                ]));

        var node = new PdfOcrExtractionNode(next, extractor);

        // Would throw inside IronOCR if the OCR path ran on the fake bytes.
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("Seite eins Inhalt\n\nSeite zwei Inhalt", dataContext.Get<string>("$.text"));
        Assert.Equal("TextLayer", dataContext.Get<string>("$.ExtractionTier"));
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_TextOnImagePages_DowngradesTierToTextLayerFromOcr()
    {
        var config = new PdfOcrExtractionNodeConfiguration
        {
            Path = "$.file",
            TargetPath = "$.text",
            PreferTextLayer = true
        };

        var (dataContext, nodeContext, next) = PrepareTest(config);

        // Scanned document with a baked-in OCR text layer ("searchable PDF"): pages have
        // BOTH a text layer and a dominating full-page image.
        var extractor = A.Fake<IPdfTextExtractor>();
        A.CallTo(() => extractor.Extract(A<byte[]>._, A<int>._))
            .Returns(new PdfTextExtractionResult(
                [
                    new PdfPageText(1, "Gescannter Vertragstext", true, IsTextOnImage: true),
                    new PdfPageText(2, "Zweite gescannte Seite", true, IsTextOnImage: true)
                ]));

        var node = new PdfOcrExtractionNode(next, extractor);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Text is still used (no re-OCR), but the tier must carry the trust downgrade.
        Assert.Equal("Gescannter Vertragstext\n\nZweite gescannte Seite", dataContext.Get<string>("$.text"));
        Assert.Equal("TextLayerFromOcr", dataContext.Get<string>("$.ExtractionTier"));
    }

    [Fact]
    public async Task ProcessObjectAsync_PreferTextLayerOff_ExtractorNeverCalled()
    {
        var config = new PdfOcrExtractionNodeConfiguration
        {
            Path = "$.file",
            TargetPath = "$.text",
            // Ladder explicitly off (PreferTextLayer defaults to true):
            // pre-existing OCR-only behavior.
            PreferTextLayer = false,
            ContinueOnError = true // the fake bytes make IronOCR fail; node must swallow it
        };

        var (dataContext, nodeContext, next) = PrepareTest(config);

        var extractor = A.Fake<IPdfTextExtractor>();
        var node = new PdfOcrExtractionNode(next, extractor);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Backwards compatibility: without the opt-in, the text-layer extractor is not involved.
        A.CallTo(() => extractor.Extract(A<byte[]>._, A<int>._)).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    // ---- Page selection for the OCR pass (pure helpers) ----

    [Fact]
    public void PagesWithoutLayer_ReturnsZeroBasedIndicesOfPagesLackingALayer_InDocumentOrder()
    {
        var pages = new List<PdfPageText>
        {
            new(1, "digital", true),
            new(2, "", false),
            new(3, "digital", true),
            new(4, "", false)
        };

        var indices = PdfOcrExtractionNode.PagesWithoutLayer(pages);

        Assert.Equal([1, 3], indices);
    }

    [Fact]
    public void SelectedPageIndices_ConvertsOneBasedConfigurationToSortedDistinctZeroBasedIndices()
    {
        var config = new PdfOcrExtractionNodeConfiguration { Path = "$.file", TargetPath = "$.text", PageNumbers = [3, 1, 3, 0] };

        var indices = PdfOcrExtractionNode.SelectedPageIndices(config);

        Assert.Equal([0, 2], indices);
    }

    [Fact]
    public void SelectedPageIndices_NoConfiguredPages_ReturnsNullForWholeDocument()
    {
        var config = new PdfOcrExtractionNodeConfiguration { Path = "$.file", TargetPath = "$.text" };

        Assert.Null(PdfOcrExtractionNode.SelectedPageIndices(config));
    }

    [Fact]
    public void MergeMixedPages_ConsumesOcrTextsSequentiallyForThePagesWithoutALayer()
    {
        var pages = new List<PdfPageText>
        {
            new(1, "page one (layer)", true),
            new(2, "", false),
            new(3, "page three (layer)", true),
            new(4, "", false)
        };

        // OCR ran only for pages 2 and 4, so its results are NOT indexable by page number.
        var merged = PdfOcrExtractionNode.MergeMixedPages(pages, ["page two (ocr)", "page four (ocr)"]);

        Assert.Equal("page one (layer)\n\npage two (ocr)\n\npage three (layer)\n\npage four (ocr)", merged);
    }

    [Fact]
    public void MergeMixedPages_MissingOcrResult_LeavesThatPageEmptyWithoutShiftingOthers()
    {
        var pages = new List<PdfPageText>
        {
            new(1, "", false),
            new(2, "layer", true),
            new(3, "", false)
        };

        var merged = PdfOcrExtractionNode.MergeMixedPages(pages, ["only one ocr result"]);

        Assert.Equal("only one ocr result\n\nlayer\n\n", merged);
    }
}

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
/// Tests the extraction-ladder wiring of <see cref="PdfOcrExtractionNode"/> (AB#4464)
/// with a faked <see cref="IPdfTextExtractor"/>: when every page has a text layer, the
/// node must use it verbatim, report tier "TextLayer" and never reach the OCR path
/// (the fake PDF bytes here would make IronOCR throw — a green test proves OCR was skipped).
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
                ],
                []));

        var node = new PdfOcrExtractionNode(next, extractor);

        // Would throw inside IronOCR if the OCR path ran on the fake bytes.
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("Seite eins Inhalt\n\nSeite zwei Inhalt", dataContext.Get<string>("$.text"));
        Assert.Equal("TextLayer", dataContext.Get<string>("$.ExtractionTier"));
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmbeddedXmlRequested_WritesXmlOutput()
    {
        var config = new PdfOcrExtractionNodeConfiguration
        {
            Path = "$.file",
            TargetPath = "$.text",
            PreferTextLayer = true,
            ExtractEmbeddedXml = true
        };

        var (dataContext, nodeContext, next) = PrepareTest(config);

        const string invoiceXml = "<rsm:CrossIndustryInvoice>ZUGFeRD</rsm:CrossIndustryInvoice>";
        var extractor = A.Fake<IPdfTextExtractor>();
        A.CallTo(() => extractor.Extract(A<byte[]>._, A<int>._))
            .Returns(new PdfTextExtractionResult(
                [new PdfPageText(1, "Rechnung 4711 Betrag 119,00 EUR", true)],
                [new PdfEmbeddedFile("factur-x.xml", Encoding.UTF8.GetBytes(invoiceXml))]));

        var node = new PdfOcrExtractionNode(next, extractor);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(invoiceXml, dataContext.Get<string>("$.EmbeddedXml"));
        Assert.Equal("Rechnung 4711 Betrag 119,00 EUR", dataContext.Get<string>("$.text"));
        Assert.Equal("TextLayer", dataContext.Get<string>("$.ExtractionTier"));
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
                ],
                []));

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
            // Default configuration: ladder off, pre-existing OCR-only behavior.
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
}

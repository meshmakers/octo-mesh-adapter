using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class TransformPdfNodeTests : NodeTestBase
{
    /// <summary>Builds a valid multi-page PDF (optionally pre-rotated) and returns it base64-encoded.</summary>
    private static string MakePdfBase64(int pages = 1, int rotate = 0)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Rotate = rotate;
        }

        using var ms = new MemoryStream();
        doc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    private static PdfDocument OpenOutput(string base64)
    {
        var ms = new MemoryStream(Convert.FromBase64String(base64));
        return PdfReader.Open(ms, PdfDocumentOpenMode.Import);
    }

    [Fact]
    public async Task ProcessObjectAsync_SelectsAndReordersPagesAcrossSources()
    {
        var config = new TransformPdfNodeConfiguration
        { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakePdfBase64(2), MakePdfBase64(1) });
        // Output = source1.page0, source0.page1, source0.page0 -> 3 pages.
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 1, PageIndex = 0 },
            new() { SourceIndex = 0, PageIndex = 1 },
            new() { SourceIndex = 0, PageIndex = 0 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var outPdf = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(outPdf);
        using var doc = OpenOutput(outPdf!);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_DropsUnreferencedPages()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(3) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var outPdf = CapturedString(dataContext, config.TargetPath);
        using var doc = OpenOutput(outPdf!);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesRotation()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0, Rotate = 90 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal(90, doc.Pages[0].Rotate);
    }

    [Fact]
    public async Task ProcessObjectAsync_AddsRotationOnTopOfExisting()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // Source page already rotated 90; op adds another 90 -> expect 180.
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1, rotate: 90) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0, Rotate = 90 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal(180, doc.Pages[0].Rotate);
    }

    [Fact]
    public async Task ProcessObjectAsync_CropSetsCropBox()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        // Crop the right half at rotation 0.
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new()
            {
                SourceIndex = 0, PageIndex = 0,
                Crop = new PdfCropRect { X = 0.5, Y = 0, Width = 0.5, Height = 1 }
            },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        var media = doc.Pages[0].MediaBox;
        var crop = doc.Pages[0].CropBox;
        // Right half: llx at horizontal centre, full height.
        Assert.Equal(media.X1 + media.Width / 2, crop.X1, 1);
        Assert.Equal(media.X2, crop.X2, 1);
        Assert.Equal(media.Y1, crop.Y1, 1);
        Assert.Equal(media.Y2, crop.Y2, 1);
    }

    /// <summary>PDF whose single page already carries a CropBox (a previous edit round).</summary>
    private static string MakeCroppedPdfBase64(PdfRectangle cropBox, int rotate = 0)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Rotate = rotate;
        page.CropBox = cropBox;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    [Fact]
    public async Task ProcessObjectAsync_CropOnAlreadyCroppedPage_NestsInsideExistingCropBox()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // Viewers only show the existing CropBox area, so a new crop of the
        // "right half" must select the right half of THAT box, not of the MediaBox.
        var existing = new PdfRectangle(new XPoint(100, 100), new XPoint(500, 700));
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakeCroppedPdfBase64(existing) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new()
            {
                SourceIndex = 0, PageIndex = 0,
                Crop = new PdfCropRect { X = 0.5, Y = 0, Width = 0.5, Height = 1 }
            },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        var crop = doc.Pages[0].CropBox;
        Assert.Equal(300, crop.X1, 1); // 100 + 0.5 * 400
        Assert.Equal(500, crop.X2, 1);
        Assert.Equal(100, crop.Y1, 1);
        Assert.Equal(700, crop.Y2, 1);
    }

    [Fact]
    public async Task ProcessObjectAsync_CropOnRotatedCroppedPage_UsesExistingCropBoxAsReference()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // The field case: page displayed at 270° with a CropBox from a previous
        // round. Selecting the full visible area must reproduce the existing box.
        var existing = new PdfRectangle(new XPoint(65, 505), new XPoint(519, 769));
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakeCroppedPdfBase64(existing, rotate: 270) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new()
            {
                SourceIndex = 0, PageIndex = 0,
                Crop = new PdfCropRect { X = 0, Y = 0, Width = 1, Height = 1 }
            },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        var crop = doc.Pages[0].CropBox;
        Assert.Equal(65, crop.X1, 1);
        Assert.Equal(505, crop.Y1, 1);
        Assert.Equal(519, crop.X2, 1);
        Assert.Equal(769, crop.Y2, 1);
        Assert.Equal(270, doc.Pages[0].Rotate);
    }

    [Fact]
    public async Task ProcessObjectAsync_Uncrop_RestoresFullPage()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var existing = new PdfRectangle(new XPoint(100, 100), new XPoint(500, 700));
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakeCroppedPdfBase64(existing) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0, Uncrop = true },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        var page = doc.Pages[0];
        Assert.Equal(page.MediaBox.X1, page.CropBox.X1, 1);
        Assert.Equal(page.MediaBox.Y1, page.CropBox.Y1, 1);
        Assert.Equal(page.MediaBox.X2, page.CropBox.X2, 1);
        Assert.Equal(page.MediaBox.Y2, page.CropBox.Y2, 1);
    }

    [Fact]
    public async Task ProcessObjectAsync_UncropWithNewCrop_IsRelativeToFullPage()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var existing = new PdfRectangle(new XPoint(100, 100), new XPoint(500, 700));
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakeCroppedPdfBase64(existing) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new()
            {
                SourceIndex = 0, PageIndex = 0, Uncrop = true,
                Crop = new PdfCropRect { X = 0.5, Y = 0, Width = 0.5, Height = 1 }
            },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        var page = doc.Pages[0];
        var mediaWidth = Math.Abs(page.MediaBox.Width);
        // Right half of the FULL page, not of the discarded old crop box.
        Assert.Equal(page.MediaBox.X1 + mediaWidth / 2, page.CropBox.X1, 1);
        Assert.Equal(page.MediaBox.X2, page.CropBox.X2, 1);
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidRotation_Throws()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0, Rotate = 45 },
        });

        var node = new TransformPdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PageIndexOutOfRange_Throws()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 5 },
        });

        var node = new TransformPdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_SourceIndexOutOfRange_Throws()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 3, PageIndex = 0 },
        });

        var node = new TransformPdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_SkipsUnreadableSource_WhenNotFailing()
    {
        var config = new TransformPdfNodeConfiguration
        { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out", FailOnInvalidPdf = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var notAPdf = Convert.ToBase64String("hello world"u8.ToArray());
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakePdfBase64(1), notAPdf });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0 },
            new() { SourceIndex = 1, PageIndex = 0 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        using var doc = OpenOutput(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyOps_Throws()
    {
        var config = new TransformPdfNodeConfiguration { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(1) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>());

        var node = new TransformPdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_ScratchMode_WritesReference()
    {
        await using var scratchSpace = new PipelineScratchSpace();
        var config = new TransformPdfNodeConfiguration
        { Path = "$.pdfs", OpsPath = "$.ops", TargetPath = "$.out", OutputAsScratchFile = true };
        var (dataContext, nodeContext, next) = PrepareTest(config, scratchSpace: scratchSpace);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?> { MakePdfBase64(2) });
        A.CallTo(() => dataContext.GetArray<PdfPageOp>("$.ops")).Returns(new List<PdfPageOp?>
        {
            new() { SourceIndex = 0, PageIndex = 0 },
            new() { SourceIndex = 0, PageIndex = 1 },
        });

        var node = new TransformPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var setCall = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == config.TargetPath);
        var reference = Assert.IsType<ScratchFileReference>(setCall.Arguments[1]);
        Assert.False(string.IsNullOrEmpty(reference.Token));
        Assert.True(reference.Length > 0);

        await using var read = scratchSpace.OpenRead(reference.Token!);
        using var doc = PdfReader.Open(read, PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
    }
}

public class PdfCropGeometryTests
{
    [Theory]
    [InlineData(0, 0.2, 0.3, 0.2, 0.3)]
    [InlineData(90, 0.2, 0.3, 0.3, 0.8)]
    [InlineData(180, 0.2, 0.3, 0.8, 0.7)]
    [InlineData(270, 0.2, 0.3, 0.7, 0.2)]
    public void DisplayToUnrotated_MapsCorners(int rotation, double dx, double dy, double ex, double ey)
    {
        var (x, y) = PdfCropGeometry.DisplayToUnrotated(dx, dy, rotation);
        Assert.Equal(ex, x, 6);
        Assert.Equal(ey, y, 6);
    }

    [Fact]
    public void DisplayRectToCropBox_RightHalf_NoRotation()
    {
        var media = new PdfRectangle(new XPoint(0, 0), new XPoint(600, 800));
        var box = PdfCropGeometry.DisplayRectToCropBox(media, 0,
            new PdfCropRect { X = 0.5, Y = 0, Width = 0.5, Height = 1 });
        Assert.Equal(300, box.X1, 6);
        Assert.Equal(0, box.Y1, 6);
        Assert.Equal(600, box.X2, 6);
        Assert.Equal(800, box.Y2, 6);
    }

    [Fact]
    public void DisplayRectToCropBox_TopHalf_NoRotation_MapsToUpperPdfHalf()
    {
        var media = new PdfRectangle(new XPoint(0, 0), new XPoint(600, 800));
        // Display top half (top-left origin) -> upper half in PDF space (y-up).
        var box = PdfCropGeometry.DisplayRectToCropBox(media, 0,
            new PdfCropRect { X = 0, Y = 0, Width = 1, Height = 0.5 });
        Assert.Equal(0, box.X1, 6);
        Assert.Equal(400, box.Y1, 6);
        Assert.Equal(600, box.X2, 6);
        Assert.Equal(800, box.Y2, 6);
    }
}

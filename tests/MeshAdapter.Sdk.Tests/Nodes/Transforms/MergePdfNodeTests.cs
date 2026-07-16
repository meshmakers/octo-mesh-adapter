using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MergePdfNodeTests : NodeTestBase
{
    /// <summary>Builds a valid single-page PDF and returns it base64-encoded.</summary>
    private static string MakePdfBase64(int pages = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            doc.AddPage();
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

    private static int PageCountOf(string base64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(base64));
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Fact]
    public async Task ProcessObjectAsync_MergesTwoPdfs_IntoAllPages()
    {
        var config = new MergePdfNodeConfiguration
            { Path = "$.pdfs", TargetPath = "$.merged", ContentLengthTargetPath = "$.mergedLen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakePdfBase64(1), MakePdfBase64(2) });

        var node = new MergePdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var merged = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(merged);
        Assert.Equal(3, PageCountOf(merged!));
        var len = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == "$.mergedLen").Arguments[1];
        Assert.Equal((long)Convert.FromBase64String(merged!).Length, len);
    }

    [Fact]
    public async Task ProcessObjectAsync_SkipsInvalidPdf_WhenNotFailing()
    {
        var config = new MergePdfNodeConfiguration
            { Path = "$.pdfs", TargetPath = "$.merged", FailOnInvalidPdf = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // Second entry is valid base64 but not a PDF -> import fails -> skipped.
        var notAPdf = Convert.ToBase64String("hello world"u8.ToArray());
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { MakePdfBase64(1), notAPdf });

        var node = new MergePdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var merged = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(merged);
        Assert.Equal(1, PageCountOf(merged!));
    }

    [Fact]
    public async Task ProcessObjectAsync_InvalidPdf_ThrowsWhenFailOnInvalid()
    {
        var config = new MergePdfNodeConfiguration
            { Path = "$.pdfs", TargetPath = "$.merged", FailOnInvalidPdf = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var notAPdf = Convert.ToBase64String("hello world"u8.ToArray());
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs"))
            .Returns(new List<string?> { notAPdf });

        var node = new MergePdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyInput_Throws()
    {
        var config = new MergePdfNodeConfiguration { Path = "$.pdfs", TargetPath = "$.merged" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.pdfs")).Returns(new List<string?>());

        var node = new MergePdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

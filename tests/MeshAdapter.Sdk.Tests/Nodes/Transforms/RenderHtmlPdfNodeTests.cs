using System.Text;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class RenderHtmlPdfNodeTests : NodeTestBase
{
    // A 1x1 transparent PNG as a data URI — exercises the inline-image path.
    private const string PngDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M8AAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    private static void AssertIsPdf(string? base64)
    {
        Assert.NotNull(base64);
        var bytes = Convert.FromBase64String(base64!);
        Assert.True(bytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task ProcessObjectAsync_RichHtml_RendersPdf()
    {
        const string html = """
            <html><body>
              <h1>Invoice INV-2025-042</h1>
              <p>Dear customer, <b>thank you</b> for your <i>order</i>.</p>
              <ul><li>Item A</li><li>Item B</li></ul>
              <table>
                <tr><th>Position</th><th>Amount</th></tr>
                <tr><td>Consulting</td><td>1.200,00 EUR</td></tr>
              </table>
              <blockquote>Please pay within 14 days.</blockquote>
              <p><a href="https://example.com">View online</a></p>
            </body></html>
            """;
        var config = new RenderHtmlPdfNodeConfiguration
            { Path = "$.html", TargetPath = "$.pdf", Title = "Forwarded mail", ContentLengthTargetPath = "$.pdfLen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var base64 = CapturedString(dataContext, config.TargetPath);
        AssertIsPdf(base64);
        var len = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == "$.pdfLen").Arguments[1];
        Assert.Equal((long)Convert.FromBase64String(base64!).Length, len);
    }

    [Fact]
    public async Task ProcessObjectAsync_InlineDataUriImage_RendersPdf()
    {
        var html = $"<p>Logo:</p><img src=\"{PngDataUri}\" alt=\"logo\"/><p>End.</p>";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.html")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.html")).Returns(html);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_PlainText_RendersPdf()
    {
        const string text = "Simple forwarded note\nSecond line\nThird line";
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.body", TargetPath = "$.pdf", IsHtml = false };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.body")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.body")).Returns(text);

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyContent_RendersPdf()
    {
        var config = new RenderHtmlPdfNodeConfiguration { Path = "$.html", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        // GetKind returns Undefined (default) → content resolves to empty string.

        var node = new RenderHtmlPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        AssertIsPdf(CapturedString(dataContext, config.TargetPath));
    }
}

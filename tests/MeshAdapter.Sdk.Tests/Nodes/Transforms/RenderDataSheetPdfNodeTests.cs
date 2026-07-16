using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class RenderDataSheetPdfNodeTests : NodeTestBase
{
    private static JsonObject SampleModel()
    {
        return new JsonObject
        {
            ["title"] = "BMD handover cover sheet",
            ["subtitle"] = "RE-2025-001",
            ["sections"] = new JsonArray(
                new JsonObject
                {
                    ["heading"] = "Document",
                    ["rows"] = new JsonArray(
                        new JsonObject { ["label"] = "Number", ["value"] = "RE-2025-001" },
                        new JsonObject { ["label"] = "Gross", ["value"] = "1.200,00 EUR" })
                },
                new JsonObject
                {
                    ["heading"] = "Vendor",
                    ["rows"] = new JsonArray(
                        new JsonObject { ["label"] = "Name", ["value"] = "Contoso GmbH" })
                }),
            ["footerHeading"] = "Note to tax advisor",
            ["footerText"] = "Please book against IT expenses."
        };
    }

    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    [Fact]
    public async Task ProcessObjectAsync_RendersPdf_WithPdfSignature()
    {
        var config = new RenderDataSheetPdfNodeConfiguration
            { Path = "$.model", TargetPath = "$.pdf", ContentLengthTargetPath = "$.pdfLen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<JsonNode>("$.model")).Returns(SampleModel());

        var node = new RenderDataSheetPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var base64 = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(base64);
        var bytes = Convert.FromBase64String(base64!);
        // Every PDF starts with "%PDF".
        Assert.True(bytes.Length > 4);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var len = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == "$.pdfLen").Arguments[1];
        Assert.Equal((long)bytes.Length, len);
    }

    [Fact]
    public async Task ProcessObjectAsync_RendersPdf_WithMinimalModel()
    {
        var config = new RenderDataSheetPdfNodeConfiguration { Path = "$.model", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<JsonNode>("$.model"))
            .Returns(new JsonObject { ["title"] = "Only a title" });

        var node = new RenderDataSheetPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var base64 = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(base64);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(base64!), 0, 4));
    }

    [Fact]
    public async Task ProcessObjectAsync_ModelNotAnObject_Throws()
    {
        var config = new RenderDataSheetPdfNodeConfiguration { Path = "$.model", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<JsonNode>("$.model")).Returns(null);

        var node = new RenderDataSheetPdfNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }
}

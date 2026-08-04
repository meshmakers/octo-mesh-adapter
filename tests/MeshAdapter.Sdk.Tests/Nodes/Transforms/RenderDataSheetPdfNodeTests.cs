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
    public async Task ProcessObjectAsync_FormatsNumericRowValues_WithCultureAndSuffix()
    {
        // Amount rows must render with the model's number format and the
        // currency appended (e.g. "1.186,96 EUR") so BMD document recognition
        // parses them as amounts — a raw JSON double ("1186.96") is not
        // recognized. The explicit separators (not the culture) define the
        // output: CI and production containers may lack ICU and then fall
        // back to invariant globalization for any culture name.
        var model = new JsonObject
        {
            ["title"] = "Cover sheet",
            ["culture"] = "de-AT",
            ["numberDecimalSeparator"] = ",",
            ["numberGroupSeparator"] = ".",
            ["sections"] = new JsonArray(
                new JsonObject
                {
                    ["heading"] = "Amounts",
                    ["rows"] = new JsonArray(
                        new JsonObject
                        {
                            ["label"] = "Gross",
                            ["value"] = JsonNode.Parse("1186.96"),
                            ["format"] = "N2",
                            ["suffix"] = "EUR"
                        },
                        // Missing value: no formatting, no dangling suffix.
                        new JsonObject
                        {
                            ["label"] = "Net", ["value"] = "", ["format"] = "N2", ["suffix"] = "EUR"
                        },
                        // Non-numeric value with a format falls back to the raw string.
                        new JsonObject
                        {
                            ["label"] = "Note", ["value"] = "n/a", ["format"] = "N2"
                        })
                })
        };
        var config = new RenderDataSheetPdfNodeConfiguration { Path = "$.model", TargetPath = "$.pdf" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<JsonNode>("$.model")).Returns(model);

        var node = new RenderDataSheetPdfNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var base64 = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(base64);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Convert.FromBase64String(base64!));
        var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));
        Assert.Contains("1.186,96 EUR", text);
        Assert.DoesNotContain("1186.96", text);
        Assert.Contains("n/a", text);
        // The empty Net row must not render a lone "EUR" suffix: exactly one EUR on the sheet.
        Assert.Equal(1, text.Split("EUR").Length - 1);
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

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class CreateZipArchiveNodeTests : NodeTestBase
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string? CapturedString(IDataContext dataContext, string targetPath)
    {
        var call = Fake.GetCalls(dataContext)
            .FirstOrDefault(c => c.Method.Name == "Set"
                                 && c.Arguments.Count >= 2
                                 && (string?)c.Arguments[0] == targetPath);
        return call?.Arguments[1] as string;
    }

    private static Dictionary<string, string> ReadZip(string base64)
    {
        var result = new Dictionary<string, string>();
        using var ms = new MemoryStream(Convert.FromBase64String(base64));
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }

        return result;
    }

    [Fact]
    public async Task ProcessObjectAsync_BundlesEntries_IncludingFolders()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject { ["fileName"] = "AP/RE-2025-001.pdf", ["contentBase64"] = B64("first") },
            new JsonObject { ["fileName"] = "AR/RG-2025-050.pdf", ["contentBase64"] = B64("second") },
            new JsonObject { ["fileName"] = "index.txt", ["contentBase64"] = B64("toc") });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = new CreateZipArchiveNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var zip = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(zip);
        var contents = ReadZip(zip!);
        Assert.Equal(3, contents.Count);
        Assert.Equal("first", contents["AP/RE-2025-001.pdf"]);
        Assert.Equal("second", contents["AR/RG-2025-050.pdf"]);
        Assert.Equal("toc", contents["index.txt"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_LeadingSlashTrimmed()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject { ["fileName"] = "/AP/x.pdf", ["contentBase64"] = B64("x") });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = new CreateZipArchiveNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        Assert.True(contents.ContainsKey("AP/x.pdf"));
    }

    [Fact]
    public async Task ProcessObjectAsync_EntryMissingContent_Throws()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(new JsonObject { ["fileName"] = "a.pdf" });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = new CreateZipArchiveNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_NotAnArray_Throws()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries"))
            .Returns(new JsonObject { ["not"] = "an array" });

        var node = new CreateZipArchiveNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

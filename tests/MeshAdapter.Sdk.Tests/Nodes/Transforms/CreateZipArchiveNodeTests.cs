using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class CreateZipArchiveNodeTests : NodeTestBase
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static CreateZipArchiveNode NewNode(NodeDelegate next) =>
        new(next, A.Fake<IMeshEtlContext>());

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
    public async Task ProcessObjectAsync_VerbatimEntry_KeepsNameAndStaysOutOfManifest()
    {
        var config = new CreateZipArchiveNodeConfiguration
        {
            Path = "$.entries", TargetPath = "$.zip",
            AppendSequenceNumber = true, ManifestFileName = "belege.csv",
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject
            {
                ["fileName"] = "AP/RE-2025-001.pdf", ["contentBase64"] = B64("doc"),
                ["manifest"] = new JsonObject { ["Belegnummer"] = "RE-2025-001" },
            },
            new JsonObject
            {
                ["fileName"] = "lieferanten.csv", ["contentBase64"] = B64("vendors"),
                ["verbatim"] = true,
            });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        // Content entry got its running number; the verbatim entry kept its exact name.
        Assert.Contains("AP/RE-2025-001_001.pdf", contents.Keys);
        Assert.Contains("lieferanten.csv", contents.Keys);
        Assert.Equal("vendors", contents["lieferanten.csv"]);
        // The manifest lists only the content entry — no row for the verbatim file.
        Assert.Contains("RE-2025-001", contents["belege.csv"]);
        Assert.DoesNotContain("lieferanten", contents["belege.csv"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_BundlesEntries_IncludingFolders()
    {
        var config = new CreateZipArchiveNodeConfiguration
            { Path = "$.entries", TargetPath = "$.zip", ContentLengthTargetPath = "$.zipLen" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject { ["fileName"] = "AP/RE-2025-001.pdf", ["contentBase64"] = B64("first") },
            new JsonObject { ["fileName"] = "AR/RG-2025-050.pdf", ["contentBase64"] = B64("second") },
            new JsonObject { ["fileName"] = "index.txt", ["contentBase64"] = B64("toc") });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        var zip = CapturedString(dataContext, config.TargetPath);
        Assert.NotNull(zip);
        // the byte length is emitted and matches the decoded archive
        var len = Fake.GetCalls(dataContext).First(c => c.Method.Name == "Set"
            && (string?)c.Arguments[0] == "$.zipLen").Arguments[1];
        Assert.Equal((long)Convert.FromBase64String(zip!).Length, len);
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

        var node = NewNode(next);
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

        var node = NewNode(next);
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

        var node = NewNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_ScratchEntry_IsStreamedIntoArchive()
    {
        await using var scratchSpace = new PipelineScratchSpace();
        var token = scratchSpace.CreateFile("pdf");
        await using (var write = scratchSpace.OpenWrite(token))
        {
            await write.WriteAsync("scratch-content"u8.ToArray(), CancellationToken.None);
        }

        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config, scratchSpace: scratchSpace);
        var entries = new JsonArray(
            new JsonObject { ["fileName"] = "AP/from-scratch.pdf", ["scratchFileToken"] = token },
            new JsonObject { ["fileName"] = "inline.txt", ["contentBase64"] = B64("inline") });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal("scratch-content", contents["AP/from-scratch.pdf"]);
        Assert.Equal("inline", contents["inline.txt"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_ScratchEntry_WithoutScratchSpace_Throws()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config); // no scratch space
        var entries = new JsonArray(
            new JsonObject { ["fileName"] = "x.pdf", ["scratchFileToken"] = "some-token" });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_PathSegments_AreSanitizedAndJoined()
    {
        var config = new CreateZipArchiveNodeConfiguration { Path = "$.entries", TargetPath = "$.zip" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject
            {
                // vendor name with a slash and invalid chars must NOT create extra folders
                ["pathSegments"] = new JsonArray("GJ 2026", "2026-01", "Eingangsrechnungen",
                    "2026-01-15_Miet/Pacht: GmbH?.pdf"),
                ["contentBase64"] = B64("doc")
            });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal("doc", contents["GJ 2026/2026-01/Eingangsrechnungen/2026-01-15_Miet_Pacht_ GmbH_.pdf"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppendSequenceNumber_MakesDuplicateNamesUnique()
    {
        var config = new CreateZipArchiveNodeConfiguration
            { Path = "$.entries", TargetPath = "$.zip", AppendSequenceNumber = true };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject
            {
                ["pathSegments"] = new JsonArray("2026-01", "2026-01-15_ACME.pdf"),
                ["contentBase64"] = B64("first")
            },
            new JsonObject
            {
                ["pathSegments"] = new JsonArray("2026-01", "2026-01-15_ACME.pdf"),
                ["contentBase64"] = B64("second")
            },
            new JsonObject { ["fileName"] = "no-extension", ["contentBase64"] = B64("third") });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        Assert.Equal("first", contents["2026-01/2026-01-15_ACME_001.pdf"]);
        Assert.Equal("second", contents["2026-01/2026-01-15_ACME_002.pdf"]);
        Assert.Equal("third", contents["no-extension_003"]);
    }

    [Fact]
    public async Task ProcessObjectAsync_Manifest_ListsFinalNamesAndFields()
    {
        var config = new CreateZipArchiveNodeConfiguration
        {
            Path = "$.entries", TargetPath = "$.zip", AppendSequenceNumber = true,
            ManifestFileName = "belege.csv", ManifestFileNameColumn = "Dateiname"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var entries = new JsonArray(
            new JsonObject
            {
                ["pathSegments"] = new JsonArray("Eingangsrechnungen", "2026-01-15_ACME.pdf"),
                ["contentBase64"] = B64("a"),
                ["manifest"] = new JsonObject
                {
                    ["Datum"] = "2026-01-15", ["Belegnummer"] = "RE-1",
                    ["LieferantKunde"] = "ACME; \"Söhne\" GmbH", ["Brutto"] = "119.00"
                }
            },
            new JsonObject
            {
                ["pathSegments"] = new JsonArray("Ausgangsrechnungen", "2026-02-01_Kunde.pdf"),
                ["contentBase64"] = B64("b"),
                ["manifest"] = new JsonObject
                    { ["Datum"] = "2026-02-01", ["Belegnummer"] = "AR-7", ["Netto"] = "100.00" }
            });
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = NewNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var contents = ReadZip(CapturedString(dataContext, config.TargetPath)!);
        var csv = contents["belege.csv"];
        var lines = csv.TrimEnd().Split("\r\n");
        // header = file-name column + ordered union of manifest keys
        Assert.Equal("Dateiname;Datum;Belegnummer;LieferantKunde;Brutto;Netto", lines[0].TrimStart('\uFEFF'));
        Assert.Equal(
            "Eingangsrechnungen/2026-01-15_ACME_001.pdf;2026-01-15;RE-1;\"ACME; \"\"Söhne\"\" GmbH\";119.00;",
            lines[1]);
        Assert.Equal("Ausgangsrechnungen/2026-02-01_Kunde_002.pdf;2026-02-01;AR-7;;;100.00", lines[2]);
        Assert.Equal(3, lines.Length);
        // the manifest itself carries no sequence number
        Assert.False(contents.ContainsKey("belege_001.csv"));
    }

    [Fact]
    public async Task ProcessObjectAsync_PersistMode_WithoutRootFolder_Throws()
    {
        await using var scratchSpace = new PipelineScratchSpace();
        var config = new CreateZipArchiveNodeConfiguration
            { Path = "$.entries", TargetPath = "$.zip", PersistAsFileSystemItem = true };
        var (dataContext, nodeContext, next) = PrepareTest(config, scratchSpace: scratchSpace);
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries"))
            .Returns(new JsonArray(new JsonObject { ["fileName"] = "a.pdf", ["contentBase64"] = B64("a") }));

        var node = NewNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PersistMode_WithoutScratchSpace_Throws()
    {
        var config = new CreateZipArchiveNodeConfiguration
        {
            Path = "$.entries", TargetPath = "$.zip",
            PersistAsFileSystemItem = true, RootFolderWellKnownName = "Documents"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config); // no scratch space
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries"))
            .Returns(new JsonArray(new JsonObject { ["fileName"] = "a.pdf", ["contentBase64"] = B64("a") }));

        var node = NewNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

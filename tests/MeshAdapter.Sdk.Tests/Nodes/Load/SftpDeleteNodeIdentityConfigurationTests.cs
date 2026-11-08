using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// AB#8284 compatibility: the expected identity of <c>SftpDelete@1</c> is five new optional
/// properties. A definition that does not use them loads and behaves as before, and one that
/// does loads with the values exactly as written.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class SftpDeleteNodeIdentityConfigurationTests(PipelineSchemaFixture schema)
{
    [Fact]
    public void OnIdentityMismatch_DefaultsToFail_TheZeroMember()
    {
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = "sftp-server-1", RemotePath = "/out/A.TXT" };

        Assert.Equal(IdentityMismatchHandling.Fail, config.OnIdentityMismatch);
        Assert.Equal(0, (int)IdentityMismatchHandling.Fail);
        Assert.Equal(IdentityMismatchHandling.Fail, default(IdentityMismatchHandling));
    }

    [Fact]
    public void IdentityOptions_DefaultToNotSet()
    {
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = "sftp-server-1", RemotePath = "/out/A.TXT" };

        Assert.Null(config.ExpectedLength);
        Assert.Null(config.ExpectedLengthPath);
        Assert.Null(config.ExpectedLastWriteTimeUtc);
        Assert.Null(config.ExpectedLastWriteTimeUtcPath);
    }

    [Fact]
    public void OnIdentityMismatch_UndefinedValue_IsRejected()
    {
        var config = new SftpDeleteNodeConfiguration { ServerConfiguration = "sftp-server-1", RemotePath = "/out/A.TXT" };

        var ex = Assert.Throws<ArgumentException>(() => config.OnIdentityMismatch = (IdentityMismatchHandling)2);
        Assert.Contains("Fail or Warn", ex.Message);
    }

    [Fact]
    public void Schema_NewPropertiesAreOptional_AndTheExistingOnesAreStillThere()
    {
        var node = schema.NodeSchema("SftpDelete@1");
        var properties = node["properties"]!.AsObject().Select(p => p.Key).ToHashSet();

        foreach (var added in new[]
                 {
                     "expectedLength", "expectedLengthPath", "expectedLastWriteTimeUtc",
                     "expectedLastWriteTimeUtcPath", "onIdentityMismatch"
                 })
        {
            Assert.Contains(added, properties);
        }

        foreach (var existing in new[]
                     { "type", "serverConfiguration", "remotePath", "remotePathPath", "onMissingFile" })
        {
            Assert.Contains(existing, properties);
        }

        var required = node["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToArray() ?? [];
        Assert.DoesNotContain("expectedLength", required);
        Assert.DoesNotContain("expectedLengthPath", required);
        Assert.DoesNotContain("expectedLastWriteTimeUtc", required);
        Assert.DoesNotContain("expectedLastWriteTimeUtcPath", required);
        Assert.DoesNotContain("onIdentityMismatch", required);
    }

    public static TheoryData<string> Pipelines =>
    [
        // a definition written before the identity options existed
        """
        transformations:
          - type: SftpDelete@1
            serverConfiguration: sftp-server-1
            remotePathPath: $.handled.fullPath
            onMissingFile: Ignore
        """,
        // the identity from the listing element inside the loop
        """
        transformations:
          - type: SftpDelete@1
            serverConfiguration: sftp-server-1
            remotePathPath: $.key.fullPath
            expectedLengthPath: $.key.length
            expectedLastWriteTimeUtcPath: $.key.lastWriteTimeUtc
            onMissingFile: Ignore
            onIdentityMismatch: Warn
        """
    ];

    [Theory]
    [MemberData(nameof(Pipelines))]
    public async Task Pipeline_IsAcceptedByThePipelineSerializer(string yaml)
    {
        var root = await Serializer().DeserializeAsync(yaml);

        Assert.IsType<SftpDeleteNodeConfiguration>(Assert.Single(root.Transformations!));
    }

    [Theory]
    [InlineData("expectedLastWriteTimeUtc: 2026-11-05T09:58:12.0000000Z")]
    [InlineData("expectedLastWriteTimeUtc: \"2026-11-05T09:58:12.0000000Z\"")]
    public async Task StaticIdentity_ArrivesExactlyAsWritten(string timeLine)
    {
        var yaml = $"""
                    transformations:
                      - type: SftpDelete@1
                        serverConfiguration: sftp-server-1
                        remotePath: /out/AR00001.TXT
                        expectedLength: 5000000000
                        {timeLine}
                    """;

        var root = await Serializer().DeserializeAsync(yaml);

        // A YAML timestamp could be read as a date and printed back in another form; the string
        // property keeps the text, and the node parses it with its own rule.
        var config = Assert.IsType<SftpDeleteNodeConfiguration>(Assert.Single(root.Transformations!));
        Assert.Equal(5_000_000_000, config.ExpectedLength);
        Assert.Equal("2026-11-05T09:58:12.0000000Z", config.ExpectedLastWriteTimeUtc);
        Assert.Equal(IdentityMismatchHandling.Fail, config.OnIdentityMismatch);
    }

    [Theory]
    [InlineData("expectedLenght", "1234")]
    [InlineData("expectedLastWriteTimeUTC", "2026-11-05T09:58:12Z")]
    [InlineData("onIdentityMissmatch", "Warn")]
    public async Task MisspelledIdentityOption_IsRefusedWhenThePipelineLoads_NamingTheOption(string misspelledKey,
        string value)
    {
        // A misspelled option is not dropped silently: if it were, a definition with both identity
        // options misspelled would load and delete without a check.
        var yaml = $"""
                    transformations:
                      - type: SftpDelete@1
                        serverConfiguration: sftp-server-1
                        remotePath: /out/AR00001.TXT
                        {misspelledKey}: {value}
                    """;

        var ex = await Assert.ThrowsAsync<PipelineSerializationException>(() => Serializer().DeserializeAsync(yaml));

        Assert.Contains($"Property '{misspelledKey}' not found on type '{typeof(SftpDeleteNodeConfiguration).FullName}'",
            ex.Message, StringComparison.Ordinal);
    }

    private static IPipelineConfigurationSerializer Serializer()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        return services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
    }
}

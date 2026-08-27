using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// What a real pipeline definition produces, which a C# object initializer cannot reach: an
/// initializer applies only when the key is <em>absent</em>, so a definition carrying an explicit
/// null hands the node a value nobody wrote.
/// </summary>
public class RenderDelimitedTextConfigurationDeserializationTests : NodeTestBase
{
    private static async Task<RenderDelimitedTextNodeConfiguration> DeserializeAsync(string yaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(RenderDelimitedTextNode));
        var serializer = services.BuildServiceProvider()
            .GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + yaml);
        return root.Transformations!.OfType<RenderDelimitedTextNodeConfiguration>().Single();
    }

    [Fact]
    public async Task Deserialize_OptionsOmitted_UsesDocumentedDefaults()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                columns:
                  - value: "X"
                  - valuePath: $.id
            """);

        Assert.Equal("|", config.Delimiter);
        Assert.Equal(DelimitedLineEnding.Lf, config.LineEnding);
        Assert.True(config.TrailingNewLine);
        Assert.Equal(DelimiterInValueHandling.Fail, config.OnDelimiterInValue);
        Assert.Equal(string.Empty, config.Replacement);
        Assert.Equal(2, config.Columns!.Count);
        Assert.False(config.Columns.First().Required);
    }
}

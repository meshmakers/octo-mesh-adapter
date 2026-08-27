using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter;
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

    [Fact]
    public async Task Deserialize_ExplicitNullDelimiter_FailsTheNodeInsteadOfGluingColumns()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                delimiter: null
                columns:
                  - value: "A"
                  - value: "B"
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task Deserialize_ExplicitNullColumns_FailsTheNode()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                columns: null
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task Deserialize_ExplicitNullColumnValue_RendersAnEmptyColumn()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                trailingNewLine: false
                columns:
                  - value: null
                  - value: "B"
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{}]}"""));
        var written = DelimitedTextTestContext.CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("|B", written());
    }

    /// <summary>
    /// A configuration mistake must not leave a half-written document behind: nothing is written
    /// and the chain does not continue before the preflight has passed.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_PreflightFails_NothingIsWritten()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                delimiter: ""
                columns:
                  - value: "A"
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));

        A.CallTo(() => dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }
}

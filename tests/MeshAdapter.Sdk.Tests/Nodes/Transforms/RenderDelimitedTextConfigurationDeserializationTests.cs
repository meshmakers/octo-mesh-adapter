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
public class RenderDelimitedTextConfigurationDeserializationTests
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

    /// <summary>
    /// Asserted through the rendered document rather than on the property values: the options that
    /// carry a default are nullable and resolved where they are read, so an unset one is null on
    /// the record by design and only the behaviour says what the default actually is.
    /// </summary>
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
        Assert.Equal(string.Empty, config.Replacement);
        Assert.Equal(2, config.Columns!.Count);
        Assert.False(config.Columns.First().Required);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{"id":"1"}]}"""));
        var written = DelimitedTextTestContext.CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Lf and a trailing separator; the pipe is the default delimiter.
        Assert.Equal("X|1\n", written());
    }

    /// <summary>The default handling is Fail: a value carrying the delimiter is refused.</summary>
    [Fact]
    public async Task Deserialize_OptionsOmitted_DefaultsToFailingOnADelimiterInAValue()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                columns:
                  - valuePath: $.v
            """);

        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = "a|b" }) };
        var (dataContext, nodeContext, next) = DelimitedTextTestContext.Prepare(config, data);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
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
    /// and the chain does not continue before the preflight has passed. The write assertion is
    /// method-name based on purpose - binding one closed generic (Set&lt;string&gt;, five
    /// arguments) would let Set&lt;JsonNode&gt; and the two-argument overload slip past it.
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

        DelimitedTextTestContext.AssertNothingWritten(dataContext);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    /// <summary>
    /// The documented default has to survive an explicit null, and the loss is silent otherwise:
    /// the deserializer turns <c>trailingNewLine: null</c> (and a bare <c>trailingNewLine:</c>)
    /// into false, so the document quietly loses its final record separator.
    /// </summary>
    [Theory]
    [InlineData("trailingNewLine: null")]
    [InlineData("trailingNewLine:")]
    public async Task Deserialize_ExplicitNullTrailingNewLine_KeepsTheDocumentedDefault(string line)
    {
        var config = await DeserializeAsync($"""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                {line}
                columns:
                  - valuePath: $.id
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{"id":"1"}]}"""));
        var written = DelimitedTextTestContext.CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("1\n", written());
    }

    [Theory]
    [InlineData("lineEnding: null")]
    [InlineData("onDelimiterInValue: null")]
    public async Task Deserialize_ExplicitNullEnumOption_KeepsTheDocumentedDefault(string line)
    {
        var config = await DeserializeAsync($"""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                {line}
                columns:
                  - valuePath: $.v
            """);

        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = "a|b" }) };
        var (dataContext, nodeContext, next) = DelimitedTextTestContext.Prepare(config, data);

        // Defaults are Lf and Fail: a value carrying the delimiter must still fail loudly.
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>
    /// An out-of-range enum deserializes without complaint and would otherwise land in whichever
    /// branch the switch happens to end in - here that meant silently rewriting values instead of
    /// failing on them.
    /// </summary>
    [Fact]
    public async Task Deserialize_UndefinedOnDelimiterInValue_ThrowsInsteadOfPickingABranch()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                onDelimiterInValue: 5
                columns:
                  - valuePath: $.v
            """);

        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = "a|b" }) };
        var (dataContext, nodeContext, next) = DelimitedTextTestContext.Prepare(config, data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("onDelimiterInValue", ex.Message, StringComparison.Ordinal);
        DelimitedTextTestContext.AssertNothingWritten(dataContext);
    }

    [Fact]
    public async Task Deserialize_UndefinedLineEnding_ThrowsInsteadOfPickingABranch()
    {
        var config = await DeserializeAsync("""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                lineEnding: 7
                columns:
                  - valuePath: $.id
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{"id":"1"}]}"""));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("lineEnding", ex.Message, StringComparison.Ordinal);
        DelimitedTextTestContext.AssertNothingWritten(dataContext);
    }

    /// <summary>
    /// An empty or blank target path is not a harmless mistake: the data context treats an empty
    /// path as a write to the document root, so the rendered document would REPLACE the entire
    /// pipeline data and the chain would continue without a word
    /// (<c>DataContextImpl.Set</c>: <c>path == "$" || string.IsNullOrEmpty(path)</c>).
    /// A pipeline definition reaches these values even though the property is non-nullable,
    /// because the deserializer writes an explicit null over the initializer.
    /// </summary>
    [Theory]
    [InlineData("targetPath: null")]
    [InlineData("""targetPath: "" """)]
    [InlineData("""targetPath: "   " """)]
    [InlineData("path: null")]
    [InlineData("""path: "" """)]
    public async Task ProcessObjectAsync_BlankPathOrTargetPath_ThrowsAndWritesNothing(string pathLine)
    {
        var config = await DeserializeAsync($"""
              - type: RenderDelimitedText@1
                path: $.rows
                targetPath: $.text
                {pathLine}
                columns:
                  - value: "A"
            """);

        var (dataContext, nodeContext, next) =
            DelimitedTextTestContext.Prepare(config, JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));

        DelimitedTextTestContext.AssertNothingWritten(dataContext);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }
}

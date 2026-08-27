using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class RenderDelimitedTextNodeTests
{
    /// <summary>
    /// Builds a real <see cref="DataContextImpl" /> over the test data, wrapped by FakeItEasy so
    /// that reads (SelectMatches/GetKind/Get) run against the real implementation while Set calls
    /// can be observed. This exercises the genuine path-resolution behavior end-to-end.
    /// </summary>
    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        RenderDelimitedTextNodeConfiguration config, JsonNode? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var data = testData ?? new JsonObject();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(data.ToJsonString()));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("RenderDelimitedText", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private static Func<string?> CaptureWrite(IDataContext dataContext,
        RenderDelimitedTextNodeConfiguration config)
    {
        string? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                written = value);
        return () => written;
    }

    private static RenderDelimitedTextNodeConfiguration Config(params DelimitedColumn[] columns) =>
        new()
        {
            Path = "$.rows",
            TargetPath = "$.text",
            Columns = columns
        };

    [Fact]
    public async Task ProcessObjectAsync_ConstantPathAndEmptyColumns_RendersOneRowPerElement()
    {
        var config = Config(
            new DelimitedColumn { Value = "A" },
            new DelimitedColumn(),                              // reserved, always empty
            new DelimitedColumn { ValuePath = "$.id" },
            new DelimitedColumn { ValuePath = "$.missing" });    // absent -> empty

        var data = JsonNode.Parse("""{"rows":[{"id":"1"},{"id":"2"}]}""");
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        var node = new RenderDelimitedTextNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("A||1|\nA||2|\n", written());
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_ColumnsEmpty_Throws()
    {
        var config = Config();
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        var node = new RenderDelimitedTextNode(next);
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_ColumnWithValueAndValuePath_Throws()
    {
        var config = Config(new DelimitedColumn { Value = "A", ValuePath = "$.id" });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        var node = new RenderDelimitedTextNode(next);
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_CrLfLineEnding_UsesCarriageReturnAndLineFeed()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" });
        config.LineEnding = DelimitedLineEnding.CrLf;
        var data = JsonNode.Parse("""{"rows":[{"id":"1"},{"id":"2"}]}""");
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("1\r\n2\r\n", written());
    }

    [Fact]
    public async Task ProcessObjectAsync_TrailingNewLineDisabled_LastRowHasNoSeparator()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" });
        config.TrailingNewLine = false;
        var data = JsonNode.Parse("""{"rows":[{"id":"1"},{"id":"2"}]}""");
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("1\n2", written());
    }
}

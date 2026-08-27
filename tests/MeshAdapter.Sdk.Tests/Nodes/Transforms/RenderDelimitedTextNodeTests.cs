using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class RenderDelimitedTextNodeTests
{
    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        RenderDelimitedTextNodeConfiguration config, JsonNode? testData) =>
        DelimitedTextTestContext.Prepare(config, testData);

    private static Func<string?> CaptureWrite(IDataContext dataContext,
        RenderDelimitedTextNodeConfiguration config) =>
        DelimitedTextTestContext.CaptureWrite(dataContext, config);

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

    [Theory]
    [InlineData("""{"rows":[{"v":"text"}]}""", "text")]
    [InlineData("""{"rows":[{"v":35}]}""", "35")]
    [InlineData("""{"rows":[{"v":1.62}]}""", "1.62")]
    [InlineData("""{"rows":[{"v":0.00}]}""", "0.00")]
    [InlineData("""{"rows":[{"v":true}]}""", "True")]
    [InlineData("""{"rows":[{"v":false}]}""", "False")]
    [InlineData("""{"rows":[{"v":null}]}""", "")]
    [InlineData("""{"rows":[{}]}""", "")]
    public async Task ProcessObjectAsync_ScalarKinds_RenderPerHouseRule(string json, string expected)
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" });
        config.TrailingNewLine = false;
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse(json));
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(expected, written());
    }

    [Theory]
    [InlineData("""{"rows":[{"v":{"a":1}}]}""")]
    [InlineData("""{"rows":[{"v":[1,2]}]}""")]
    public async Task ProcessObjectAsync_NonScalarValue_Throws(string json)
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse(json));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("$.v", ex.Message);
    }

    /// <summary>
    /// A guard downstream of this node compares the written text against "". A skipped write would
    /// leave the path absent, the comparison reads null, and "null is not empty" is TRUE - so the
    /// delivery such a guard exists to stop would run. Assert the write, never the absence of one.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_WritesAnEmptyStringAndContinues()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        var writes = 0;
        string? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, string? v, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
            {
                writes++;
                written = v;
            });

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(1, writes);
        Assert.Equal(string.Empty, written);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("""{"rows":{"id":"1"}}""")]     // object instead of array
    [InlineData("""{"rows":"text"}""")]          // scalar instead of array
    [InlineData("""{}""")]                        // path absent entirely
    public async Task ProcessObjectAsync_PathIsNotAnArray_Throws(string json)
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse(json));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public async Task ProcessObjectAsync_ValueBreaksTheStructure_FailsByDefault(string value)
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" },
            new DelimitedColumn { Value = "tail" });
        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = value }) };
        var (dataContext, nodeContext, next) = PrepareTest(config, data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("record 0", ex.Message);
        Assert.Contains("column 0", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_ReplaceHandling_SubstitutesAndKeepsTheColumnCount()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" },
            new DelimitedColumn { Value = "tail" });
        config.OnDelimiterInValue = DelimiterInValueHandling.Replace;
        config.Replacement = "-";
        config.TrailingNewLine = false;
        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = "a|b" }) };
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("a-b|tail", written());
    }

    [Fact]
    public async Task ProcessObjectAsync_StripHandling_RemovesTheOffendingCharacters()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" },
            new DelimitedColumn { Value = "tail" });
        config.OnDelimiterInValue = DelimiterInValueHandling.Strip;
        config.TrailingNewLine = false;
        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = "a|b\nc" }) };
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("abc|tail", written());
    }

    /// <summary>
    /// The message is asserted, not just the throw: an empty delimiter also makes the replacement
    /// check trip (every string contains the empty string), so a bare ThrowsAsync would still pass
    /// with the delimiter guard removed - and report the wrong cause to whoever reads the log.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a\nb")]
    public async Task ProcessObjectAsync_UnusableDelimiter_Throws(string? delimiter)
    {
        var config = Config(new DelimitedColumn { Value = "A" });
        config.Delimiter = delimiter;
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("delimiter", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessObjectAsync_ReplacementContainsTheDelimiter_Throws()
    {
        var config = Config(new DelimitedColumn { Value = "A" });
        config.OnDelimiterInValue = DelimiterInValueHandling.Replace;
        config.Replacement = "|";
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>A constant is checked too - a layout can misconfigure one just as easily.</summary>
    [Fact]
    public async Task ProcessObjectAsync_ConstantCarriesTheDelimiter_FailsByDefault()
    {
        var config = Config(new DelimitedColumn { Value = "a|b" });
        var (dataContext, nodeContext, next) = PrepareTest(config,
            JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData("""{"rows":[{}]}""")]            // absent
    [InlineData("""{"rows":[{"id":null}]}""")]   // explicit null
    [InlineData("""{"rows":[{"id":""}]}""")]     // present but empty
    public async Task ProcessObjectAsync_RequiredColumnWithoutValue_Throws(string json)
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id", Required = true });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse(json));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("record 0", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_RequiredColumnWithValue_Renders()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id", Required = true });
        config.TrailingNewLine = false;
        var (dataContext, nodeContext, next) =
            PrepareTest(config, JsonNode.Parse("""{"rows":[{"id":"1"}]}"""));
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("1", written());
    }

    [Fact]
    public async Task ProcessObjectAsync_ColumnWithoutRequired_KeepsAnEmptyValue()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" },
            new DelimitedColumn { Value = "tail" });
        config.TrailingNewLine = false;
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[{}]}"""));
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("|tail", written());
    }
}

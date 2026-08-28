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

        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        // Exactly one write, and it carries the empty document - not "no write happened".
        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
        Assert.Equal(string.Empty, written());
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

    /// <summary>
    /// A multi-character delimiter cannot be held to the guarantee this node makes. Cleaning can
    /// SYNTHESISE it (removing "ab" from "aabb" leaves "ab"), and the Fail check looks at one value
    /// at a time, so a delimiter composed across the join boundary is invisible to it. The
    /// counterpart reader splits on the first character only, so a multi-character delimiter would
    /// not round-trip either. One character removes the whole class.
    /// </summary>
    [Theory]
    [InlineData("||")]
    [InlineData("ab")]
    [InlineData(";;")]
    public async Task ProcessObjectAsync_MultiCharacterDelimiter_IsRefused(string delimiter)
    {
        var config = Config(new DelimitedColumn { Value = "A" });
        config.Delimiter = delimiter;
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[]}"""));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("delimiter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An array of scalars is still an array, so the source check passes, but no value path can
    /// resolve against a scalar record: every read column renders empty while the constants print,
    /// producing structurally valid and entirely contentless rows. That is the "silently empty"
    /// outcome this node exists to prevent, so it fails naming the record.
    /// </summary>
    [Theory]
    [InlineData("""{"rows":["a","b"]}""")]
    [InlineData("""{"rows":[1,2]}""")]
    [InlineData("""{"rows":[["a"],["b"]]}""")]
    [InlineData("""{"rows":[null]}""")]
    public async Task ProcessObjectAsync_RecordIsNotAnObject_Throws(string json)
    {
        var config = Config(new DelimitedColumn { Value = "A" },
            new DelimitedColumn { ValuePath = "$.id" });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse(json));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("record 0", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// If the source ever reports records that the iteration does not yield, the node must not
    /// answer with an empty document and a green run - that is indistinguishable from a
    /// legitimately empty batch and would lose the records without a trace. The divergence is
    /// simulated here rather than provoked, because a healthy context never produces it.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_SourceReportsRecordsButIterationYieldsNone_Throws()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.id" });
        var (dataContext, nodeContext, next) =
            PrepareTest(config, JsonNode.Parse("""{"rows":[{"id":"1"},{"id":"2"}]}"""));

        A.CallTo(() => dataContext.SelectMatches("$.rows[*]")).Returns([]);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("2 record(s)", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    /// <summary>
    /// A malformed path escapes as a raw path-parser failure that names neither the node nor the
    /// column, so it is parsed once up front instead - the sibling that formats from paths wraps
    /// the same failure the same way.
    /// </summary>
    [Theory]
    [InlineData("$.items[")]
    [InlineData("$.items[?(@.x")]
    [InlineData("$[")]
    public async Task ProcessObjectAsync_MalformedValuePath_IsAConfigurationError(string valuePath)
    {
        var config = Config(new DelimitedColumn { Value = "A" },
            new DelimitedColumn { ValuePath = valuePath });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[{}]}"""));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("column 1", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_MalformedSourcePath_IsAConfigurationError()
    {
        var config = Config(new DelimitedColumn { Value = "A" });
        config.Path = "$.rows[";
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[{}]}"""));

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>
    /// A column value is one value. A wildcard, filter or recursive-descent path selects a set,
    /// and which member of it lands in the column would depend on the record shape - worse, the
    /// same path can resolve on one backing store and silently render empty on another. Refused
    /// rather than guessed.
    /// </summary>
    [Theory]
    [InlineData("$.items[*].name")]
    [InlineData("$..name")]
    [InlineData("$.items[?(@.id=='1')].name")]
    [InlineData("$.*")]
    public async Task ProcessObjectAsync_MultiValuedValuePath_IsRefused(string valuePath)
    {
        var config = Config(new DelimitedColumn { ValuePath = valuePath });
        var (dataContext, nodeContext, next) = PrepareTest(config, JsonNode.Parse("""{"rows":[{}]}"""));

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("column 0", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ordinary dotted and indexed paths stay legal - that is what layouts use.</summary>
    [Theory]
    [InlineData("$.id")]
    [InlineData("id")]
    [InlineData("$.nested.id")]
    [InlineData("$.list[0]")]
    public async Task ProcessObjectAsync_SimpleValuePath_IsAccepted(string valuePath)
    {
        var config = Config(new DelimitedColumn { ValuePath = valuePath });
        config.TrailingNewLine = false;
        var (dataContext, nodeContext, next) = PrepareTest(config,
            JsonNode.Parse("""{"rows":[{"id":"1","nested":{"id":"2"},"list":["3"]}]}"""));
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(written());
    }

    /// <summary>
    /// The one value guaranteed to carry structural characters is also the one that can be
    /// arbitrarily long, and the message travels into logs and execution results. Record and column
    /// index are what diagnosis needs; the value is kept only as a sample.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_StructureBreakingValueIsHuge_MessageStaysBounded()
    {
        var config = Config(new DelimitedColumn { ValuePath = "$.v" });
        var huge = new string('x', 5000) + "|" + new string('y', 5000);
        var data = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["v"] = huge }) };
        var (dataContext, nodeContext, next) = PrepareTest(config, data);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.True(ex.Message.Length < 600, $"message was {ex.Message.Length} characters");
        Assert.Contains("record 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("column 0", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// A fixed 34-column layout of the kind this node exists for: two constants, six value columns
    /// and 26 reserved columns that always render empty. Pins the exact bytes, including the
    /// trailing line feed, and that an absent optional value leaves an empty column rather than
    /// swallowing one.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_FixedThirtyFourColumnLayout_ProducesTheExactDocument()
    {
        var config = FixedLayoutConfig();
        var data = JsonNode.Parse("""
            {"rows":[
              {"key":"4269","label":"First item","code":"TW_001","gtin":"4270004042719","unit":"pc.","amount":"35"},
              {"key":"28607","label":"Second item","code":"TW_003","unit":"pc.","amount":"0"}
            ]}
            """);
        var (dataContext, nodeContext, next) = PrepareTest(config, data);
        var written = CaptureWrite(dataContext, config);

        await new RenderDelimitedTextNode(next).ProcessObjectAsync(dataContext, nodeContext);

        const string expected =
            "A*||4269|First item|TW_001||||||4270004042719|pc.||||||||35|||1|||||||||||\n" +
            "A*||28607|Second item|TW_003|||||||pc.||||||||0|||1|||||||||||\n";

        var text = written();
        Assert.Equal(expected, text);
        Assert.All(text!.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Equal(34, line.Split('|').Length));
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    private static RenderDelimitedTextNodeConfiguration FixedLayoutConfig()
    {
        var columns = new List<DelimitedColumn>();
        for (var i = 1; i <= 34; i++)
        {
            columns.Add(i switch
            {
                1 => new DelimitedColumn { Value = "A*" },
                3 => new DelimitedColumn { ValuePath = "$.key", Required = true },
                4 => new DelimitedColumn { ValuePath = "$.label" },
                5 => new DelimitedColumn { ValuePath = "$.code" },
                11 => new DelimitedColumn { ValuePath = "$.gtin" },
                12 => new DelimitedColumn { ValuePath = "$.unit" },
                20 => new DelimitedColumn { ValuePath = "$.amount" },
                23 => new DelimitedColumn { Value = "1" },
                _ => new DelimitedColumn()
            });
        }

        return new RenderDelimitedTextNodeConfiguration
        {
            Path = "$.rows",
            TargetPath = "$.text",
            Columns = columns
        };
    }
}

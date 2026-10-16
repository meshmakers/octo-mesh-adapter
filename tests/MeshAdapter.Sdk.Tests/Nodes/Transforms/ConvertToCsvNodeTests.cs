using System.Text;
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

public class ConvertToCsvNodeTests
{
    /// <summary>
    /// A real <see cref="DataContextImpl" /> over the test data, so reads run through the genuine
    /// path resolution; the node's writes are captured and still reach the real context.
    /// </summary>
    private sealed class Run
    {
        public required IDataContext DataContext { get; init; }
        public required INodeContext NodeContext { get; init; }
        public required NodeDelegate Next { get; init; }
        public string? WrittenText { get; set; }
        public long? WrittenLength { get; set; }
    }

    private static Run Prepare(ConvertToCsvNodeConfiguration config, string json)
    {
        var logger = A.Fake<IPipelineLogger>();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(json));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        var root = NodeContext.CreateRootNodeContext(
            new ServiceCollection().BuildServiceProvider(), logger, dataContext);
        var nodeContext = root.RegisterChildNode("ConvertToCsv", 0, config, dataContext);

        var run = new Run { DataContext = dataContext, NodeContext = nodeContext, Next = A.Fake<NodeDelegate>() };

        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                run.WrittenText = value)
            .CallsWrappedMethod();

        if (config.ContentLengthTargetPath is not null)
        {
            A.CallTo(() => dataContext.Set(config.ContentLengthTargetPath, A<long>._, A<DocumentModes>._,
                    A<ValueKinds>._, A<TargetValueWriteModes>._))
                .Invokes((string _, long value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                    run.WrittenLength = value)
                .CallsWrappedMethod();
        }

        return run;
    }

    private static async Task<string?> RenderAsync(ConvertToCsvNodeConfiguration config, string json)
    {
        var run = Prepare(config, json);
        await new ConvertToCsvNode(run.Next).ProcessObjectAsync(run.DataContext, run.NodeContext);
        return run.WrittenText;
    }

    private static ConvertToCsvNodeConfiguration Config(params CsvColumn[] columns) =>
        new() { Path = "$.rows", TargetPath = "$.csv", Columns = columns.Length == 0 ? null : columns };

    // ---------- Shape of the document ----------

    [Fact]
    public async Task NoColumns_TakesTheScalarPropertiesInOrderOfAppearance()
    {
        var csv = await RenderAsync(Config(),
            """{"rows":[{"id":1,"name":"a"},{"id":2,"name":"b","extra":true}]}""");

        // "extra" first appears in the second record and is added after the earlier columns;
        // the first record has no value for it.
        Assert.Equal("id;name;extra\r\n1;a;\r\n2;b;true\r\n", csv);
    }

    [Fact]
    public async Task NoColumns_NestedValuesAreLeftOut()
    {
        var csv = await RenderAsync(Config(),
            """{"rows":[{"id":1,"detail":{"x":1},"tags":[1,2],"name":"a"}]}""");

        Assert.Equal("id;name\r\n1;a\r\n", csv);
    }

    [Fact]
    public async Task Columns_HeaderDefaultsToTheLastNameOfThePath()
    {
        var csv = await RenderAsync(
            Config(new CsvColumn { ValuePath = "$.customer.name" }, new CsvColumn { ValuePath = "$.id", Header = "Nr" }),
            """{"rows":[{"id":7,"customer":{"name":"ACME"}}]}""");

        Assert.Equal("name;Nr\r\nACME;7\r\n", csv);
    }

    [Fact]
    public async Task Columns_ConstantAndEmptyColumns()
    {
        var csv = await RenderAsync(
            Config(
                new CsvColumn { Header = "k", Value = "VALID" },
                new CsvColumn { Header = "reserved" },
                new CsvColumn { Header = "id", ValuePath = "$.id" }),
            """{"rows":[{"id":1},{"id":2}]}""");

        Assert.Equal("k;reserved;id\r\nVALID;;1\r\nVALID;;2\r\n", csv);
    }

    [Fact]
    public async Task IncludeHeaderFalse_WritesNoHeaderRow()
    {
        var config = Config(new CsvColumn { Header = "id", ValuePath = "$.id" });
        config.IncludeHeader = false;

        Assert.Equal("1\r\n2\r\n", await RenderAsync(config, """{"rows":[{"id":1},{"id":2}]}"""));
    }

    [Fact]
    public async Task LineEndingLf_AndNoTrailingNewLine()
    {
        var config = Config(new CsvColumn { Header = "id", ValuePath = "$.id" });
        config.LineEnding = CsvLineEnding.Lf;
        config.TrailingNewLine = false;

        Assert.Equal("id\n1\n2", await RenderAsync(config, """{"rows":[{"id":1},{"id":2}]}"""));
    }

    [Fact]
    public async Task CustomDelimiter_Tab()
    {
        var config = Config(new CsvColumn { ValuePath = "$.a" }, new CsvColumn { ValuePath = "$.b" });
        config.Delimiter = "\t";

        Assert.Equal("a\tb\r\n1\t2\r\n", await RenderAsync(config, """{"rows":[{"a":1,"b":2}]}"""));
    }

    [Fact]
    public async Task TrailingDelimiter_EndsEveryRowIncludingTheHeader()
    {
        var config = Config(new CsvColumn { ValuePath = "$.a" }, new CsvColumn { ValuePath = "$.b" });
        config.TrailingDelimiter = true;

        Assert.Equal("a;b;\r\n1;2;\r\n", await RenderAsync(config, """{"rows":[{"a":1,"b":2}]}"""));
    }

    [Fact]
    public async Task EmptyArray_WithExplicitColumns_WritesTheHeaderOnly()
    {
        var csv = await RenderAsync(Config(new CsvColumn { Header = "id", ValuePath = "$.id" }), """{"rows":[]}""");

        Assert.Equal("id\r\n", csv);
    }

    [Fact]
    public async Task EmptyArray_WithoutColumns_WritesAnEmptyDocumentAndDoesNotSkipTheWrite()
    {
        var csv = await RenderAsync(Config(), """{"rows":[]}""");

        // Not null: a later guard compares against "" and a missing path would read as "not empty".
        Assert.Equal(string.Empty, csv);
    }

    [Fact]
    public async Task ChainContinues()
    {
        var run = Prepare(Config(new CsvColumn { ValuePath = "$.id" }), """{"rows":[{"id":1}]}""");
        await new ConvertToCsvNode(run.Next).ProcessObjectAsync(run.DataContext, run.NodeContext);

        A.CallTo(() => run.Next(run.DataContext, run.NodeContext)).MustHaveHappenedOnceExactly();
    }

    // ---------- Quoting ----------

    [Fact]
    public async Task MinimalQuoting_QuotesOnlyWhatNeedsIt_AndDoublesQuotes()
    {
        var csv = await RenderAsync(Config(new CsvColumn { Header = "t", ValuePath = "$.t" }),
            """{"rows":[{"t":"plain"},{"t":"a;b"},{"t":"say \"hi\""},{"t":"line\nbreak"},{"t":"cr\rx"}]}""");

        Assert.Equal("t\r\nplain\r\n\"a;b\"\r\n\"say \"\"hi\"\"\"\r\n\"line\nbreak\"\r\n\"cr\rx\"\r\n", csv);
    }

    [Fact]
    public async Task TextQuoting_QuotesStringsDatesAndHeader_NotNumbersOrBooleans()
    {
        var config = Config(
            new CsvColumn { Header = "s", ValuePath = "$.s" },
            new CsvColumn { Header = "n", ValuePath = "$.n" },
            new CsvColumn { Header = "b", ValuePath = "$.b" },
            new CsvColumn { Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime, Format = "yyyy-MM-dd" },
            new CsvColumn { Header = "e", ValuePath = "$.missing" });
        config.Quoting = CsvQuoting.Text;

        var csv = await RenderAsync(config, """{"rows":[{"s":"x","n":5,"b":true,"d":"2026-09-01T00:00:00Z"}]}""");

        Assert.Equal("\"s\";\"n\";\"b\";\"d\";\"e\"\r\n\"x\";5;true;\"2026-09-01\";\r\n", csv);
    }

    [Fact]
    public async Task TextQuoting_ANumberWrittenAsStringColumnIsQuoted()
    {
        var config = Config(new CsvColumn { Header = "n", ValuePath = "$.n", DataType = CsvValueType.String });
        config.Quoting = CsvQuoting.Text;

        Assert.Equal("\"n\"\r\n\"5\"\r\n", await RenderAsync(config, """{"rows":[{"n":5}]}"""));
    }

    [Fact]
    public async Task AllQuoting_QuotesEveryNonEmptyCell()
    {
        var config = Config(new CsvColumn { Header = "n", ValuePath = "$.n" }, new CsvColumn { Header = "e", ValuePath = "$.e" });
        config.Quoting = CsvQuoting.All;

        Assert.Equal("\"n\";\"e\"\r\n\"5\";\r\n", await RenderAsync(config, """{"rows":[{"n":5}]}"""));
    }

    // ---------- Numbers ----------

    [Fact]
    public async Task Number_WithoutFormat_KeepsTheJsonToken()
    {
        var csv = await RenderAsync(Config(new CsvColumn { Header = "n", ValuePath = "$.n" }),
            """{"rows":[{"n":0.50},{"n":12},{"n":-3.25e2}]}""");

        Assert.Equal("n\r\n0.50\r\n12\r\n-3.25e2\r\n", csv);
    }

    [Theory]
    [InlineData("0.546", "0.546")]
    [InlineData("0.92", "0.92")]
    [InlineData("0", "0")]
    [InlineData("14", "14")]
    [InlineData("14.000", "14")]
    [InlineData("0.0005", "0.001")]   // half away from zero
    [InlineData("-0.0005", "-0.001")]
    [InlineData("2.0004", "2")]
    [InlineData("1234567.8915", "1234567.892")]   // no thousands separator
    public async Task Number_DecimalsAndFormat_InvariantCultureAndLeadingZero(string value, string expected)
    {
        var column = new CsvColumn { Header = "n", ValuePath = "$.n", DataType = CsvValueType.Number, Decimals = 3, Format = "0.###" };

        var csv = await RenderAsync(Config(column), $$"""{"rows":[{"n":{{value}}}]}""");

        Assert.Equal($"n\r\n{expected}\r\n", csv);
    }

    [Fact]
    public async Task Number_FixedDecimalsFormat_KeepsTrailingZeros()
    {
        var column = new CsvColumn { Header = "n", ValuePath = "$.n", Decimals = 3, Format = "0.000" };

        var csv = await RenderAsync(Config(column), """{"rows":[{"n":14},{"n":0.546},{"n":0}]}""");

        Assert.Equal("n\r\n14.000\r\n0.546\r\n0.000\r\n", csv);
    }

    [Fact]
    public async Task Number_FactorIsAppliedBeforeRoundingAndFormatting()
    {
        var kwh = new CsvColumn { Header = "kWh", ValuePath = "$.v", Decimals = 3, Format = "0.###" };
        var kw = new CsvColumn { Header = "kW", ValuePath = "$.v", Factor = 4m, Decimals = 3, Format = "0.###" };

        var csv = await RenderAsync(Config(kwh, kw), """{"rows":[{"v":0.546},{"v":0.92},{"v":0},{"v":14.1}]}""");

        Assert.Equal("kWh;kW\r\n0.546;2.184\r\n0.92;3.68\r\n0;0\r\n14.1;56.4\r\n", csv);
    }

    [Fact]
    public async Task Number_AStringThatIsANumberIsAccepted_InvariantCulture()
    {
        var column = new CsvColumn { Header = "n", ValuePath = "$.n", DataType = CsvValueType.Number, Format = "0.00" };

        Assert.Equal("n\r\n1234.50\r\n", await RenderAsync(Config(column), """{"rows":[{"n":"1234.5"}]}"""));
    }

    [Fact]
    public async Task Number_NotANumber_Throws()
    {
        var column = new CsvColumn { Header = "n", ValuePath = "$.n", DataType = CsvValueType.Number };

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(column), """{"rows":[{"n":"abc"}]}"""));
    }

    // ---------- Dates ----------

    [Theory]
    [InlineData("2026-09-01T00:00:00Z", "01.09.2026 02:00")]        // CEST (UTC+2)
    [InlineData("2026-01-15T10:15:00Z", "15.01.2026 11:15")]        // CET (UTC+1)
    [InlineData("2026-09-01T00:00:00", "01.09.2026 02:00")]         // no offset: read as UTC
    [InlineData("2026-09-01T02:00:00+02:00", "01.09.2026 02:00")]   // offset given
    [InlineData("2026-03-29T00:45:00Z", "29.03.2026 01:45")]        // last quarter hour before the switch
    [InlineData("2026-03-29T01:00:00Z", "29.03.2026 03:00")]        // 02:00 does not exist
    [InlineData("2026-10-25T00:45:00Z", "25.10.2026 02:45")]        // still CEST
    [InlineData("2026-10-25T01:00:00Z", "25.10.2026 02:00")]        // the repeated hour, CET
    public async Task DateTime_IsConvertedIntoTheTimeZone(string utc, string expected)
    {
        var column = new CsvColumn
        {
            Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime, Format = "dd.MM.yyyy HH:mm", TimeZone = "Europe/Vienna"
        };

        Assert.Equal($"d\r\n{expected}\r\n", await RenderAsync(Config(column), $$"""{"rows":[{"d":"{{utc}}"}]}"""));
    }

    [Fact]
    public async Task DateTime_WithoutTimeZoneAndFormat_IsUtcIso()
    {
        var column = new CsvColumn { Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime };

        Assert.Equal("d\r\n2026-09-01T00:15:00\r\n",
            await RenderAsync(Config(column), """{"rows":[{"d":"2026-09-01T02:15:00+02:00"}]}"""));
    }

    [Fact]
    public async Task DateTime_FormatIsInvariant_NoCultureReshapesIt()
    {
        var column = new CsvColumn { Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime, Format = "dddd MMMM" };

        Assert.Equal("d\r\nTuesday September\r\n",
            await RenderAsync(Config(column), """{"rows":[{"d":"2026-09-01T12:00:00Z"}]}"""));
    }

    [Fact]
    public async Task DateTime_NotADate_Throws()
    {
        var column = new CsvColumn { Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime };

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(column), """{"rows":[{"d":"yesterday"}]}"""));
    }

    // ---------- Culture ----------

    [Fact]
    public async Task Culture_OnTheNode_GivesDecimalCommaToEveryNumberColumn()
    {
        var config = Config(
            new CsvColumn { Header = "a", ValuePath = "$.a", Decimals = 3, Format = "0.###" },
            new CsvColumn { Header = "b", ValuePath = "$.b" });          // no format: token, localized
        config.Culture = "de-AT";

        var csv = await RenderAsync(config, """{"rows":[{"a":0.546,"b":0.50},{"a":14,"b":-3.25}]}""");

        Assert.Equal("a;b\r\n0,546;0,50\r\n14;-3,25\r\n", csv);
    }

    [Fact]
    public async Task Culture_OnAColumn_OverridesTheNodeCulture()
    {
        var config = Config(
            new CsvColumn { Header = "de", ValuePath = "$.v", Format = "0.00" },
            new CsvColumn { Header = "inv", ValuePath = "$.v", Format = "0.00", Culture = "" },
            new CsvColumn { Header = "us", ValuePath = "$.v", Format = "0.00", Culture = "en-US" });
        config.Culture = "de-AT";

        Assert.Equal("de;inv;us\r\n1,50;1.50;1.50\r\n", await RenderAsync(config, """{"rows":[{"v":1.5}]}"""));
    }

    [Fact]
    public async Task Culture_ThousandsSeparatorOnlyWhenTheFormatAsksForOne()
    {
        var config = Config(
            new CsvColumn { Header = "plain", ValuePath = "$.v", Format = "0.00" },
            new CsvColumn { Header = "grouped", ValuePath = "$.v", Format = "#,##0.00" });
        config.Culture = "de-DE";

        Assert.Equal("plain;grouped\r\n1234567,89;1.234.567,89\r\n",
            await RenderAsync(config, """{"rows":[{"v":1234567.89}]}"""));
    }

    [Fact]
    public async Task Culture_NumbersStayBareUnderTextQuoting_AndTheCommaIsNotTheDelimiter()
    {
        var config = Config(new CsvColumn { Header = "n", ValuePath = "$.n", Format = "0.0" });
        config.Culture = "de-AT";
        config.Quoting = CsvQuoting.Text;

        Assert.Equal("\"n\"\r\n1,5\r\n", await RenderAsync(config, """{"rows":[{"n":1.5}]}"""));
    }

    [Fact]
    public async Task Culture_CommaDelimiterWithDecimalComma_QuotesTheNumberSoTheColumnsHold()
    {
        var config = Config(new CsvColumn { Header = "a", ValuePath = "$.a" }, new CsvColumn { Header = "b", ValuePath = "$.b" });
        config.Culture = "de-AT";
        config.Delimiter = ",";

        Assert.Equal("a,b\r\n\"1,5\",2\r\n", await RenderAsync(config, """{"rows":[{"a":1.5,"b":2}]}"""));
    }

    [Fact]
    public async Task Culture_ReadsAStringNumberInvariant_NotInTheOutputCulture()
    {
        // The source text is data, not a user's input: "1234.5" is 1234.5 whatever the target culture is.
        var config = Config(new CsvColumn { Header = "n", ValuePath = "$.n", DataType = CsvValueType.Number, Format = "0.0" });
        config.Culture = "de-AT";

        Assert.Equal("n\r\n1234,5\r\n", await RenderAsync(config, """{"rows":[{"n":"1234.5"}]}"""));
    }

    [Fact]
    public async Task Culture_AlsoShapesDateNames()
    {
        var column = new CsvColumn { Header = "d", ValuePath = "$.d", DataType = CsvValueType.DateTime, Format = "dddd, d. MMMM yyyy" };
        var config = Config(column);
        config.Culture = "de-AT";

        Assert.Equal("d\r\nDienstag, 1. September 2026\r\n",
            await RenderAsync(config, """{"rows":[{"d":"2026-09-01T12:00:00Z"}]}"""));
    }

    [Fact]
    public async Task Culture_NotSet_StaysInvariant()
    {
        var csv = await RenderAsync(Config(new CsvColumn { Header = "n", ValuePath = "$.n", Format = "0.00" }),
            """{"rows":[{"n":1.5}]}""");

        Assert.Equal("n\r\n1.50\r\n", csv);
    }

    [Theory]
    [InlineData("xx-NOWHERE")]
    [InlineData("not a culture")]
    public async Task Culture_Unknown_FailsBeforeAnythingIsWritten(string culture)
    {
        var config = Config(new CsvColumn { ValuePath = "$.n" });
        config.Culture = culture;
        var run = Prepare(config, """{"rows":[{"n":1}]}""");

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new ConvertToCsvNode(run.Next).ProcessObjectAsync(run.DataContext, run.NodeContext));
        Assert.Null(run.WrittenText);
    }

    [Fact]
    public async Task Culture_UnknownOnAColumn_Fails()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.n", Culture = "xx-NOWHERE" }), """{"rows":[{"n":1}]}"""));
    }

    [Fact]
    public async Task Culture_OnAConstant_IsRefused()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { Value = "x", Culture = "de-AT" }), """{"rows":[]}"""));
    }

    // ---------- Other value types ----------

    [Fact]
    public async Task Booleans_AreLowerCase_NullAndMissingAreEmpty()
    {
        var csv = await RenderAsync(
            Config(new CsvColumn { Header = "b", ValuePath = "$.b" }, new CsvColumn { Header = "n", ValuePath = "$.n" }),
            """{"rows":[{"b":true,"n":null},{"b":false}]}""");

        Assert.Equal("b;n\r\ntrue;\r\nfalse;\r\n", csv);
    }

    [Fact]
    public async Task ObjectOrArrayValue_Throws()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.o" }), """{"rows":[{"o":{"x":1}}]}"""));
    }

    [Fact]
    public async Task Required_EmptyValueThrows()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.id", Required = true }), """{"rows":[{"id":""}]}"""));
    }

    // ---------- Output ----------

    [Fact]
    public async Task ByteOrderMark_AsText_StartsWithFeff()
    {
        var config = Config(new CsvColumn { Header = "id", ValuePath = "$.id" });
        config.ByteOrderMark = true;

        var csv = await RenderAsync(config, """{"rows":[{"id":1}]}""");

        Assert.StartsWith("﻿id", csv);
    }

    [Fact]
    public async Task Base64_IsTheUtf8BytesIncludingTheByteOrderMark_AndTheLengthIsWritten()
    {
        var config = Config(new CsvColumn { Header = "name", ValuePath = "$.name" });
        config.ByteOrderMark = true;
        config.OutputAsBase64 = true;
        config.LineEnding = CsvLineEnding.Lf;
        config.ContentLengthTargetPath = "$.length";

        var run = Prepare(config, """{"rows":[{"name":"Müller"}]}""");
        await new ConvertToCsvNode(run.Next).ProcessObjectAsync(run.DataContext, run.NodeContext);

        var bytes = Convert.FromBase64String(run.WrittenText!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("name\nMüller\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(bytes.Length, run.WrittenLength);
    }

    [Fact]
    public async Task Base64_WithoutByteOrderMark_HasNone()
    {
        var config = Config(new CsvColumn { Header = "id", ValuePath = "$.id" });
        config.OutputAsBase64 = true;

        var csv = await RenderAsync(config, """{"rows":[{"id":1}]}""");

        Assert.Equal("id\r\n1\r\n", Encoding.UTF8.GetString(Convert.FromBase64String(csv!)));
    }

    // ---------- The grid-operator layout of AB#6403, exactly ----------

    [Fact]
    public async Task GridOperatorLayout_MatchesTheSampleByteForByte()
    {
        var config = new ConvertToCsvNodeConfiguration
        {
            Path = "$.rows",
            TargetPath = "$.csv",
            Delimiter = ";",
            LineEnding = CsvLineEnding.Lf,
            Quoting = CsvQuoting.Text,
            TrailingDelimiter = true,
            ByteOrderMark = true,
            Columns =
            [
                new CsvColumn
                {
                    Header = "Datum", ValuePath = "$.from", DataType = CsvValueType.DateTime,
                    Format = "dd.MM.yyyy HH:mm", TimeZone = "Europe/Vienna"
                },
                new CsvColumn { Header = "kWh", ValuePath = "$.kwh", Decimals = 3, Format = "0.###" },
                new CsvColumn { Header = "kW", ValuePath = "$.kwh", Factor = 4m, Decimals = 3, Format = "0.###" },
                new CsvColumn { Header = "Status", Value = "VALID" }
            ]
        };

        var csv = await RenderAsync(config, """
            {"rows":[
              {"from":"2026-08-31T22:00:00Z","kwh":0.546},
              {"from":"2026-08-31T22:15:00Z","kwh":0.92},
              {"from":"2026-08-31T22:30:00Z","kwh":0},
              {"from":"2026-08-31T22:45:00Z","kwh":0.696},
              {"from":"2026-08-31T23:00:00Z","kwh":0.858}
            ]}
            """);

        var expected = "﻿\"Datum\";\"kWh\";\"kW\";\"Status\";\n" +
                       "\"01.09.2026 00:00\";0.546;2.184;\"VALID\";\n" +
                       "\"01.09.2026 00:15\";0.92;3.68;\"VALID\";\n" +
                       "\"01.09.2026 00:30\";0;0;\"VALID\";\n" +
                       "\"01.09.2026 00:45\";0.696;2.784;\"VALID\";\n" +
                       "\"01.09.2026 01:00\";0.858;3.432;\"VALID\";\n";
        Assert.Equal(expected, csv);
    }

    // ---------- Configuration mistakes always fail ----------

    [Theory]
    [InlineData("")]
    [InlineData(";;")]
    [InlineData("\n")]
    public async Task Delimiter_MustBeOneCharacterAndNotALineBreak(string delimiter)
    {
        var config = Config(new CsvColumn { ValuePath = "$.id" });
        config.Delimiter = delimiter;

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));
    }

    [Fact]
    public async Task Delimiter_ADoubleQuoteIsRefused()
    {
        var config = Config(new CsvColumn { ValuePath = "$.id" });
        config.Delimiter = "\"";

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankPath_Throws(string path)
    {
        var config = Config(new CsvColumn { ValuePath = "$.id" });
        config.Path = path;

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankTargetPath_Throws(string path)
    {
        var config = Config(new CsvColumn { ValuePath = "$.id" });
        config.TargetPath = path;

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));
    }

    [Fact]
    public async Task SourceNotAnArray_Throws()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.id" }), """{"rows":{"id":1}}"""));
    }

    [Fact]
    public async Task RecordNotAnObject_Throws()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.id" }), """{"rows":[1,2]}"""));
    }

    [Fact]
    public async Task ColumnWithValueAndValuePath_Throws()
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { Value = "A", ValuePath = "$.id" }), """{"rows":[]}"""));
    }

    [Fact]
    public async Task NullColumnEntry_Throws()
    {
        var config = Config();
        config.Columns = new CsvColumn[] { null! };

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[]}"""));
    }

    [Theory]
    [InlineData("$.items[*].id")]
    [InlineData("$..id")]
    [InlineData("$.items[?(@.x)]")]
    [InlineData("$[")]
    public async Task ValuePathSelectingASetOrUnparsable_Throws(string valuePath)
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = valuePath }), """{"rows":[{"id":1}]}"""));
    }

    [Fact]
    public async Task UnknownTimeZone_FailsBeforeAnythingIsWritten()
    {
        var run = Prepare(
            Config(new CsvColumn { ValuePath = "$.d", DataType = CsvValueType.DateTime, TimeZone = "Nowhere/Land" }),
            """{"rows":[{"d":"2026-09-01T00:00:00Z"}]}""");

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => new ConvertToCsvNode(run.Next).ProcessObjectAsync(run.DataContext, run.NodeContext));
        Assert.Null(run.WrittenText);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public async Task Decimals_OutOfRange_Throws(int decimals)
    {
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { ValuePath = "$.n", Decimals = decimals }), """{"rows":[{"n":1}]}"""));
    }

    [Fact]
    public async Task UndefinedEnumValues_Throw()
    {
        var config = Config(new CsvColumn { ValuePath = "$.id" });
        config.Quoting = (CsvQuoting)42;
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));

        config = Config(new CsvColumn { ValuePath = "$.id" });
        config.LineEnding = (CsvLineEnding)42;
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));

        config = Config(new CsvColumn { ValuePath = "$.id", DataType = (CsvValueType)42 });
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(config, """{"rows":[{"id":1}]}"""));
    }

    [Fact]
    public async Task ValueWithTypeNumberAndFormatIsRefusedOnAConstant_NotSilentlyIgnored()
    {
        // A constant is text: a number/date format next to it would be ignored without a word.
        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => RenderAsync(Config(new CsvColumn { Value = "5", Format = "0.00" }), """{"rows":[]}"""));
    }
}

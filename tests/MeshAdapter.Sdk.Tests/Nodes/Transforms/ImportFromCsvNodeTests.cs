using System.Text;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class ImportFromCsvNodeTests
{
    private static ImportFromCsvNodeConfiguration CreateConfig(
        ICollection<CsvColumnMapping>? mappings = null,
        string delimiter = ";",
        string encoding = "utf-8",
        bool hasHeaderRow = true,
        int skipRows = 0)
    {
        return new ImportFromCsvNodeConfiguration
        {
            FileIndex = 0,
            Delimiter = delimiter,
            Encoding = encoding,
            HasHeaderRow = hasHeaderRow,
            SkipRows = skipRows,
            ColumnMappings = mappings ?? new List<CsvColumnMapping>()
        };
    }

    #region ParseCsvLine

    [Fact]
    public void ParseCsvLine_SimpleFields_SplitsCorrectly()
    {
        var result = ImportFromCsvNode.ParseCsvLine("a;b;c", ";");
        Assert.Equal(3, result.Length);
        Assert.Equal("a", result[0]);
        Assert.Equal("b", result[1]);
        Assert.Equal("c", result[2]);
    }

    [Fact]
    public void ParseCsvLine_QuotedField_HandlesDelimiterInsideQuotes()
    {
        var result = ImportFromCsvNode.ParseCsvLine("\"a;b\";c;d", ";");
        Assert.Equal(3, result.Length);
        Assert.Equal("a;b", result[0]);
        Assert.Equal("c", result[1]);
        Assert.Equal("d", result[2]);
    }

    [Fact]
    public void ParseCsvLine_EscapedQuotes_HandlesDoubleQuotes()
    {
        var result = ImportFromCsvNode.ParseCsvLine("\"a\"\"b\";c", ";");
        Assert.Equal(2, result.Length);
        Assert.Equal("a\"b", result[0]);
        Assert.Equal("c", result[1]);
    }

    [Fact]
    public void ParseCsvLine_EmptyFields_PreservesEmptyStrings()
    {
        var result = ImportFromCsvNode.ParseCsvLine("a;;c", ";");
        Assert.Equal(3, result.Length);
        Assert.Equal("a", result[0]);
        Assert.Equal("", result[1]);
        Assert.Equal("c", result[2]);
    }

    [Fact]
    public void ParseCsvLine_CommaDelimiter_SplitsCorrectly()
    {
        var result = ImportFromCsvNode.ParseCsvLine("a,b,c", ",");
        Assert.Equal(3, result.Length);
        Assert.Equal("a", result[0]);
        Assert.Equal("b", result[1]);
        Assert.Equal("c", result[2]);
    }

    #endregion

    #region SplitLines

    [Fact]
    public void SplitLines_SkipsBlankLines()
    {
        var lines = ImportFromCsvNode.SplitLines("a\n\nb\n  \nc");
        Assert.Equal(3, lines.Count);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    [Fact]
    public void SplitLines_HandlesCrLf()
    {
        var lines = ImportFromCsvNode.SplitLines("a\r\nb\r\nc");
        Assert.Equal(3, lines.Count);
    }

    #endregion

    #region DecodeFileContent

    [Fact]
    public void DecodeFileContent_Utf8_DecodesCorrectly()
    {
        var config = CreateConfig(encoding: "utf-8");
        var original = "Hello;World;Wörld";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.DecodeFileContent(base64, config, nodeContext);

        Assert.Equal(original, result);
    }

    [Fact]
    public void DecodeFileContent_Utf16Le_DecodesCorrectly()
    {
        var config = CreateConfig(encoding: "utf-16le");
        var original = "Buchungsdatum;Betrag";
        var base64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(original));
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.DecodeFileContent(base64, config, nodeContext);

        Assert.Equal(original, result);
    }

    [Fact]
    public void DecodeFileContent_Utf16LeWithBom_StripsBom()
    {
        var config = CreateConfig(encoding: "utf-16le");
        var original = "Test";
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(original)).ToArray();
        var base64 = Convert.ToBase64String(bytes);
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.DecodeFileContent(base64, config, nodeContext);

        Assert.Equal(original, result);
    }

    [Fact]
    public void DecodeFileContent_InvalidBase64_ReturnsNull()
    {
        var config = CreateConfig();
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.DecodeFileContent("not-valid-base64!!!", config, nodeContext);

        Assert.Null(result);
        A.CallTo(() => nodeContext.Error(A<string>.That.Contains("Invalid base64"))).MustHaveHappenedOnceExactly();
    }

    #endregion

    #region ConvertValue

    [Fact]
    public void ConvertValue_String_ReturnsValue()
    {
        var mapping = new CsvColumnMapping { TargetProperty = "test", DataType = CsvDataType.String };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("hello", mapping, nodeContext, 1);

        Assert.Equal("hello", result?.Value<string>());
    }

    [Fact]
    public void ConvertValue_EmptyString_ReturnsNull()
    {
        var mapping = new CsvColumnMapping { TargetProperty = "test", DataType = CsvDataType.String };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("", mapping, nodeContext, 1);

        Assert.Null(result);
    }

    [Fact]
    public void ConvertValue_Double_DeAtCulture_ParsesCorrectly()
    {
        var mapping = new CsvColumnMapping
        {
            TargetProperty = "amount", DataType = CsvDataType.Double, NumberCulture = "de-AT"
        };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("-61,48", mapping, nodeContext, 1);

        Assert.Equal(-61.48, result?.Value<double>());
    }

    [Fact]
    public void ConvertValue_Double_DeAtCulture_ParsesThousandsSeparator()
    {
        var mapping = new CsvColumnMapping
        {
            TargetProperty = "amount", DataType = CsvDataType.Double, NumberCulture = "de-AT"
        };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("28.500,00", mapping, nodeContext, 1);

        Assert.Equal(28500.00, result?.Value<double>());
    }

    [Fact]
    public void ConvertValue_DateTime_GermanFormat_ParsesCorrectly()
    {
        var mapping = new CsvColumnMapping
        {
            TargetProperty = "date", DataType = CsvDataType.DateTime, DateFormat = "dd.MM.yyyy"
        };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("13.03.2026", mapping, nodeContext, 1);

        Assert.NotNull(result);
        Assert.Equal(JTokenType.Date, result.Type);
        Assert.Equal(new DateTime(2026, 3, 13), result.Value<DateTime>());
    }

    [Fact]
    public void ConvertValue_Boolean_Zero_ReturnsFalse()
    {
        var mapping = new CsvColumnMapping { TargetProperty = "flag", DataType = CsvDataType.Boolean };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("0", mapping, nodeContext, 1);

        Assert.Equal(false, result?.Value<bool>());
    }

    [Fact]
    public void ConvertValue_Boolean_One_ReturnsTrue()
    {
        var mapping = new CsvColumnMapping { TargetProperty = "flag", DataType = CsvDataType.Boolean };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("1", mapping, nodeContext, 1);

        Assert.Equal(true, result?.Value<bool>());
    }

    [Fact]
    public void ConvertValue_Int_ParsesCorrectly()
    {
        var mapping = new CsvColumnMapping { TargetProperty = "count", DataType = CsvDataType.Int };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("42", mapping, nodeContext, 1);

        Assert.Equal(42, result?.Value<int>());
    }

    [Fact]
    public void ConvertValue_InvalidDouble_ReturnsNullAndWarns()
    {
        var mapping = new CsvColumnMapping
        {
            TargetProperty = "amount", DataType = CsvDataType.Double
        };
        var nodeContext = A.Fake<INodeContext>();

        var result = ImportFromCsvNode.ConvertValue("not-a-number", mapping, nodeContext, 1);

        Assert.Null(result);
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains("Failed to convert"),
            A<object[]>._)).MustHaveHappened();
    }

    #endregion

    #region ProcessObjectAsync (integration)

    [Fact]
    public async Task ProcessObjectAsync_FullCsvParsing_ProducesCorrectOutput()
    {
        var csvContent = "Name;Amount;Date\nAlice;-61,48;13.03.2026\nBob;100,00;14.03.2026";
        var base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(csvContent));

        var config = CreateConfig(
            mappings: new List<CsvColumnMapping>
            {
                new()
                {
                    SourceColumn = "Name", TargetProperty = "name", DataType = CsvDataType.String
                },
                new()
                {
                    SourceColumn = "Amount", TargetProperty = "amount", DataType = CsvDataType.Double,
                    NumberCulture = "de-AT"
                },
                new()
                {
                    SourceColumn = "Date", TargetProperty = "date", DataType = CsvDataType.DateTime,
                    DateFormat = "dd.MM.yyyy"
                }
            });

        var inputData = new JObject
        {
            ["files"] = new JArray
            {
                new JObject
                {
                    ["fileName"] = "test.csv",
                    ["data"] = base64Data,
                    ["encoding"] = "base64"
                }
            }
        };

        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(inputData);

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("ImportFromCsv", 0, config, dataContext);

        JToken? capturedValue = null;
        A.CallTo(() => dataContext.SetValueByPath(
            A<string>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<JArray>._)).Invokes(call => { capturedValue = call.GetArgument<JArray>(4); });

        var next = A.Fake<NodeDelegate>();
        var node = new ImportFromCsvNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedValue);
        var resultArray = (JArray)capturedValue;
        Assert.Equal(2, resultArray.Count);

        Assert.Equal("Alice", resultArray[0]?["name"]?.Value<string>());
        Assert.Equal(-61.48, resultArray[0]?["amount"]?.Value<double>());
        Assert.Equal(new DateTime(2026, 3, 13), resultArray[0]?["date"]?.Value<DateTime>());

        Assert.Equal("Bob", resultArray[1]?["name"]?.Value<string>());
        Assert.Equal(100.00, resultArray[1]?["amount"]?.Value<double>());

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSourceIndex_MapsCorrectly()
    {
        var csvContent = "a;b;c\n1;2;3";
        var base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(csvContent));

        var config = CreateConfig(
            hasHeaderRow: false,
            mappings: new List<CsvColumnMapping>
            {
                new() { SourceIndex = 0, TargetProperty = "first", DataType = CsvDataType.String },
                new() { SourceIndex = 2, TargetProperty = "third", DataType = CsvDataType.String }
            });

        var inputData = new JObject
        {
            ["files"] = new JArray
            {
                new JObject { ["fileName"] = "test.csv", ["data"] = base64Data }
            }
        };

        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(inputData);

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("ImportFromCsv", 0, config, dataContext);

        JToken? capturedValue = null;
        A.CallTo(() => dataContext.SetValueByPath(
            A<string>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<JArray>._)).Invokes(call => { capturedValue = call.GetArgument<JArray>(4); });

        var next = A.Fake<NodeDelegate>();
        var node = new ImportFromCsvNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedValue);
        var resultArray = (JArray)capturedValue;
        Assert.Equal(2, resultArray.Count);

        // First row "a;b;c" with no header means it's data
        Assert.Equal("a", resultArray[0]?["first"]?.Value<string>());
        Assert.Equal("c", resultArray[0]?["third"]?.Value<string>());
        Assert.Equal("1", resultArray[1]?["first"]?.Value<string>());
        Assert.Equal("3", resultArray[1]?["third"]?.Value<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_NoFile_ReportsError()
    {
        var config = CreateConfig(
            mappings: new List<CsvColumnMapping>
            {
                new() { SourceColumn = "Name", TargetProperty = "name" }
            });

        var inputData = new JObject();

        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(inputData);

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("ImportFromCsv", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        var node = new ImportFromCsvNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    #endregion
}

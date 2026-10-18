using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// AB#6589: <c>DataMapping@1</c> maps every element of a multi-match path (wildcard, recursive descent,
/// filter) in place; every other path keeps the single-value behaviour. These tests run against a REAL
/// <see cref="DataContextImpl" />: the behaviour under test is how the node and the data context's
/// multi-match write play together, which a faked context cannot show.
/// </summary>
public class DataMappingNodeWildcardTests
{
    private sealed record Run(
        IDataContext DataContext,
        IPipelineLogger Logger,
        NodeDelegate Next,
        INodeContext NodeContext,
        Exception? Error);

    private static async Task<Run> Execute(DataMappingNodeConfiguration config, string json)
    {
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(JsonDocument.Parse(json));
        var rootNodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("DataMapping", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();

        Exception? error = null;
        try
        {
            await new DataMappingNode(next).ProcessObjectAsync(dataContext, nodeContext);
        }
        catch (Exception e)
        {
            error = e;
        }

        return new Run(dataContext, logger, next, nodeContext, error);
    }

    private static DataMappingNodeConfiguration UnitConfig(string path, string? targetPath = null) => new()
    {
        Path = path,
        TargetPath = targetPath ?? path,
        SourceValueType = AttributeValueTypesDto.Int,
        TargetValueType = AttributeValueTypesDto.String,
        Mappings =
        [
            new MappingEntry { SourceValue = 1, TargetValue = "kWh" },
            new MappingEntry { SourceValue = 2, TargetValue = "MWh" }
        ]
    };

    private static void AssertDocument(IDataContext dataContext, string expectedJson)
    {
        var actual = dataContext.Get<JsonNode>("$");
        Assert.True(JsonNode.DeepEquals(actual, JsonNode.Parse(expectedJson)),
            $"Expected {JsonNode.Parse(expectedJson)!.ToJsonString()} but was {actual?.ToJsonString()}");
    }

    // ---- array mapping -------------------------------------------------------------------------

    [Fact]
    public async Task WildcardPath_MapsEveryElementInPlace_AndLeavesTheRestUntouched()
    {
        var run = await Execute(UnitConfig("$.sd.Rows[*].Values[5]"), """
            {"keep":{"a":1},"sd":{"Rows":[
              {"Timestamp":10,"Values":["m1",1.5,7,true,0,1]},
              {"Timestamp":20,"Values":["m2",2.5,8,false,1,2]},
              {"Timestamp":30,"Values":["m3",3.5,9,true,2,1]}
            ]}}
            """);

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """
            {"keep":{"a":1},"sd":{"Rows":[
              {"Timestamp":10,"Values":["m1",1.5,7,true,0,"kWh"]},
              {"Timestamp":20,"Values":["m2",2.5,8,false,1,"MWh"]},
              {"Timestamp":30,"Values":["m3",3.5,9,true,2,"kWh"]}
            ]}}
            """);
        A.CallTo(() => run.Next.Invoke(run.DataContext, run.NodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RealStreamDataShape_MapsUnitAndDataQualityEnumKeys()
    {
        // GetStreamData@1 output shape: Rows with Timestamp, WellKnownName and a Values array whose
        // columns have different types (string, double, int enum key Unit, int enum key DataQuality).
        const string rows = """
            {"sd":{"Headers":["Timestamp","WellKnownName","Amount.Value","Amount.Unit","DataQuality"],"Rows":[
              {"Timestamp":"2026-10-01T00:00:00Z","Values":["AT001",1.25,1,0]},
              {"Timestamp":"2026-10-01T00:15:00Z","Values":["AT001",0.75,2,1]},
              {"Timestamp":"2026-10-01T00:30:00Z","Values":["AT002",3.0,1,2]},
              {"Timestamp":"2026-10-01T00:45:00Z","Values":["AT002",4.5,2,3]},
              {"Timestamp":"2026-10-01T01:00:00Z","Values":["AT003",0.0,1,4]}
            ]}}
            """;
        var unit = UnitConfig("$.sd.Rows[*].Values[2]");
        var quality = new DataMappingNodeConfiguration
        {
            Path = "$.sd.Rows[*].Values[3]",
            TargetPath = "$.sd.Rows[*].Values[3]",
            SourceValueType = AttributeValueTypesDto.Int,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings =
            [
                new MappingEntry { SourceValue = 0, TargetValue = "Unknown" },
                new MappingEntry { SourceValue = 1, TargetValue = "L1" },
                new MappingEntry { SourceValue = 2, TargetValue = "L2" },
                new MappingEntry { SourceValue = 3, TargetValue = "L3" },
                new MappingEntry { SourceValue = 4, TargetValue = "Manual" }
            ]
        };

        // Two nodes after each other on ONE context, as a pipeline runs them.
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(JsonDocument.Parse(rows));
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger,
            dataContext);
        var next = A.Fake<NodeDelegate>();
        await new DataMappingNode(next).ProcessObjectAsync(dataContext,
            root.RegisterChildNode("DataMapping", 0, unit, dataContext));
        await new DataMappingNode(next).ProcessObjectAsync(dataContext,
            root.RegisterChildNode("DataMapping", 1, quality, dataContext));

        AssertDocument(dataContext, """
            {"sd":{"Headers":["Timestamp","WellKnownName","Amount.Value","Amount.Unit","DataQuality"],"Rows":[
              {"Timestamp":"2026-10-01T00:00:00Z","Values":["AT001",1.25,"kWh","Unknown"]},
              {"Timestamp":"2026-10-01T00:15:00Z","Values":["AT001",0.75,"MWh","L1"]},
              {"Timestamp":"2026-10-01T00:30:00Z","Values":["AT002",3.0,"kWh","L2"]},
              {"Timestamp":"2026-10-01T00:45:00Z","Values":["AT002",4.5,"MWh","L3"]},
              {"Timestamp":"2026-10-01T01:00:00Z","Values":["AT003",0.0,"kWh","Manual"]}
            ]}}
            """);
    }

    [Fact]
    public async Task ElementsWithoutMapping_StayUnchanged_AndNullElementsAreSkipped()
    {
        var run = await Execute(UnitConfig("$.items[*].unit"),
            """{"items":[{"unit":1},{"unit":null},{"unit":9},{"unit":2},{"other":true}]}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext,
            """{"items":[{"unit":"kWh"},{"unit":null},{"unit":9},{"unit":"MWh"},{"other":true}]}""");
    }

    [Fact]
    public async Task FirstMatchingMappingWins_ForEveryElement()
    {
        var config = UnitConfig("$.items[*]");
        config.Mappings =
        [
            new MappingEntry { SourceValue = 1, TargetValue = "first" },
            new MappingEntry { SourceValue = 1, TargetValue = "second" }
        ];

        var run = await Execute(config, """{"items":[1,1,3]}""");

        AssertDocument(run.DataContext, """{"items":["first","first",3]}""");
    }

    [Theory]
    [InlineData(null)] // not given: the data context's default is the document root
    [InlineData("$")]
    [InlineData("")]
    [InlineData("$.items[*]")] // the same path
    public async Task TargetPath_OmittedOrTheSamePath_IsAccepted(string? targetPath)
    {
        var config = UnitConfig("$.items[*]");
        if (targetPath is not null)
        {
            config.TargetPath = targetPath;
        }
        else
        {
            config = new DataMappingNodeConfiguration
            {
                Path = "$.items[*]",
                SourceValueType = config.SourceValueType,
                TargetValueType = config.TargetValueType,
                Mappings = config.Mappings
            };
        }

        var run = await Execute(config, """{"items":[1,2]}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"items":["kWh","MWh"]}""");
    }

    [Theory]
    [InlineData("$.items[?(@.kind=='a')].unit", """{"items":[{"kind":"a","unit":1},{"kind":"b","unit":1},{"kind":"a","unit":2}]}""",
        """{"items":[{"kind":"a","unit":"kWh"},{"kind":"b","unit":1},{"kind":"a","unit":"MWh"}]}""")] // filter
    [InlineData("$..unit", """{"x":{"unit":1},"y":[{"unit":2},{"z":{"unit":1}}]}""",
        """{"x":{"unit":"kWh"},"y":[{"unit":"MWh"},{"z":{"unit":"kWh"}}]}""")] // recursive descent
    public async Task FilterAndRecursiveDescentPaths_AreMultiMatchToo(string path, string input, string expected)
    {
        var run = await Execute(UnitConfig(path), input);

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, expected);
    }

    [Fact]
    public async Task LargeArray_IsMappedCompletely()
    {
        var rows = new JsonArray();
        for (var i = 0; i < 20_000; i++)
        {
            rows.Add(new JsonObject { ["Values"] = new JsonArray("m", i, 1 + (i % 2)) });
        }

        var run = await Execute(UnitConfig("$.Rows[*].Values[2]"), new JsonObject { ["Rows"] = rows }.ToJsonString());

        Assert.Null(run.Error);
        Assert.Equal("kWh", run.DataContext.Get<string>("$.Rows[0].Values[2]"));
        Assert.Equal("MWh", run.DataContext.Get<string>("$.Rows[1].Values[2]"));
        Assert.Equal("MWh", run.DataContext.Get<string>("$.Rows[19999].Values[2]"));
        Assert.Equal(19_999, run.DataContext.Get<int>("$.Rows[19999].Values[1]"));
    }

    // ---- error handling ------------------------------------------------------------------------

    [Fact]
    public async Task WildcardPath_WithADifferentTargetPath_IsRejected_NamingTheNodeAndBothPaths()
    {
        var run = await Execute(UnitConfig("$.items[*]", "$.mapped"), """{"items":[1,2]}""");

        var error = Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
        Assert.Contains(run.NodeContext.NodePath.ToString(), error.Message);
        Assert.Contains("$.items[*]", error.Message);
        Assert.Contains("$.mapped", error.Message);
        // Nothing was written and the chain did not continue.
        AssertDocument(run.DataContext, """{"items":[1,2]}""");
        A.CallTo(() => run.Next.Invoke(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("$.items[?(@.kind=='a')].unit", """{"items":[{"kind":"a","unit":1}]}""")]
    [InlineData("$..unit", """{"x":{"unit":1}}""")]
    public async Task FilterOrRecursivePath_WithADifferentTargetPath_IsRejected_WithoutTalkingAboutAWildcard(
        string path, string input)
    {
        var run = await Execute(UnitConfig(path, "$.mapped"), input);

        var error = Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
        Assert.Contains("multi-match", error.Message);
        Assert.DoesNotContain("without a wildcard", error.Message);
        Assert.Contains(path, error.Message);
    }

    [Theory]
    [InlineData(nameof(DataMappingNodeConfiguration.TargetValueWriteMode), "Append")]
    [InlineData(nameof(DataMappingNodeConfiguration.TargetValueWriteMode), "Merge")]
    [InlineData(nameof(DataMappingNodeConfiguration.TargetValueKind), "Array")]
    [InlineData(nameof(DataMappingNodeConfiguration.DocumentMode), "Replace")]
    public async Task MultiMatchPath_WithANonDefaultWriteSetting_IsRejected_InsteadOfSilentlyIgnoringIt(
        string setting, string value)
    {
        var config = UnitConfig("$.items[*]");
        switch (setting)
        {
            case nameof(DataMappingNodeConfiguration.TargetValueWriteMode):
                config.TargetValueWriteMode = Enum.Parse<TargetValueWriteModes>(value);
                break;
            case nameof(DataMappingNodeConfiguration.TargetValueKind):
                config.TargetValueKind = Enum.Parse<ValueKinds>(value);
                break;
            default:
                config.DocumentMode = Enum.Parse<DocumentModes>(value);
                break;
        }

        var run = await Execute(config, """{"items":[1,2]}""");

        var error = Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
        Assert.Contains(char.ToLowerInvariant(setting[0]) + setting[1..], error.Message);
        Assert.Contains($"'{value}'", error.Message);
        AssertDocument(run.DataContext, """{"items":[1,2]}""");
        A.CallTo(() => run.Next.Invoke(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MultiMatchPath_WithTheWriteSettingsSetToTheirDefaults_Works()
    {
        // The documented single-value example sets targetValueWriteMode: Overwrite; that must stay valid.
        var config = UnitConfig("$.items[*]");
        config.TargetValueWriteMode = TargetValueWriteModes.Overwrite;
        config.TargetValueKind = ValueKinds.Simple;
        config.DocumentMode = DocumentModes.Extend;

        var run = await Execute(config, """{"items":[1,2]}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"items":["kWh","MWh"]}""");
    }

    [Fact]
    public async Task WildcardPath_ThatMatchesNothing_LogsAWarning_AndContinues()
    {
        var run = await Execute(UnitConfig("$.items[*].unit"), """{"items":[]}""");

        Assert.Null(run.Error);
        A.CallTo(() => run.Logger.Warning(A<string>._, A<string>._, A<string>.That.Contains("No elements found"),
            A<object[]>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => run.Next.Invoke(run.DataContext, run.NodeContext)).MustHaveHappenedOnceExactly();
        AssertDocument(run.DataContext, """{"items":[]}""");
    }

    [Fact]
    public async Task ElementThatCannotBeReadAsTheSourceType_FailsTheNode_NamingTheElement()
    {
        var run = await Execute(UnitConfig("$.items[*]"), """{"items":[1,"abc",2]}""");

        var error = Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
        Assert.Contains("element 1", error.Message);
        Assert.Contains("$.items[*]", error.Message);
    }

    [Fact]
    public async Task ElementFailureMidway_FailsTheNode_AndLeavesTheEarlierElementsMapped_ByDesign()
    {
        // The run is not atomic: UpdateMatchesAsync writes each element back as it is done (a second pass
        // to stage first would about double the cost and make the node slower than ForEach@1). This test
        // pins that contract so a change to it is a decision and not an accident.
        var run = await Execute(UnitConfig("$.items[*]"), """{"items":[1,2,"abc",1]}""");

        Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
        A.CallTo(() => run.Next.Invoke(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        AssertDocument(run.DataContext, """{"items":["kWh","MWh","abc",1]}""");
    }

    [Fact]
    public async Task ElementThatIsASecretMarker_IsRefused()
    {
        var run = await Execute(UnitConfig("$.items[*]"), """{"items":[1,{"isSet":true}]}""");

        Assert.IsAssignableFrom<PipelineExecutionException>(run.Error);
    }

    [Fact]
    public async Task MappingEntries_AreConvertedLazily_LikeTheSingleValueRoute()
    {
        // The unconvertible entry sits BEHIND the matching one. The single-value route never reaches it,
        // and neither may the element-wise route - not even once per run.
        var config = UnitConfig("$.items[*]");
        config.Mappings =
        [
            new MappingEntry { SourceValue = 1, TargetValue = "kWh" },
            new MappingEntry { SourceValue = "not a number", TargetValue = "x" }
        ];

        var run = await Execute(config, """{"items":[1,1]}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"items":["kWh","kWh"]}""");
    }

    [Fact]
    public async Task UnconvertibleMappingEntry_FailsLikeTheSingleValueRoute_WhenItIsReached()
    {
        var config = UnitConfig("$.items[*]");
        config.Mappings = [new MappingEntry { SourceValue = "not a number", TargetValue = "x" }];

        var run = await Execute(config, """{"items":[1]}""");

        Assert.IsType<ArgumentOutOfRangeException>(run.Error);
    }

    [Fact]
    public async Task ReportsOneSummary_NotOneDebugLinePerElement()
    {
        var run = await Execute(UnitConfig("$.items[*]"), """{"items":[1,2,1,2,1,2]}""");

        A.CallTo(() => run.Logger.Debug(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => run.Logger.Debug(A<string>._, A<string>._, A<string>.That.Contains("Mapped {0} of {1}"),
            A<object[]>._)).MustHaveHappenedOnceExactly();
    }

    // ---- regression: a path without wildcard behaves as before ---------------------------------

    public static TheoryData<AttributeValueTypesDto, string, object, AttributeValueTypesDto, object, string>
        SingleValueCases => new()
    {
        { AttributeValueTypesDto.Int, "5", 5, AttributeValueTypesDto.String, "five", "\"five\"" },
        { AttributeValueTypesDto.String, "\"on\"", "on", AttributeValueTypesDto.Int, 1, "1" },
        { AttributeValueTypesDto.Boolean, "true", true, AttributeValueTypesDto.String, "yes", "\"yes\"" },
        { AttributeValueTypesDto.Double, "2.5", 2.5, AttributeValueTypesDto.String, "two and a half", "\"two and a half\"" },
        { AttributeValueTypesDto.Int64, "9000000000", 9000000000L, AttributeValueTypesDto.String, "big", "\"big\"" },
        { AttributeValueTypesDto.String, "\"a\"", "a", AttributeValueTypesDto.Boolean, true, "true" },
        { AttributeValueTypesDto.Int, "3", 3, AttributeValueTypesDto.Double, 0.5, "0.5" },
        { AttributeValueTypesDto.Int, "3", 3, AttributeValueTypesDto.Int64, 7L, "7" },
    };

    [Theory]
    [MemberData(nameof(SingleValueCases))]
    public async Task PathWithoutWildcard_MapsTheSingleValueAsBefore(AttributeValueTypesDto sourceType,
        string sourceJson, object sourceValue, AttributeValueTypesDto targetType, object targetValue,
        string expectedJson)
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.value",
            TargetPath = "$.mapped",
            SourceValueType = sourceType,
            TargetValueType = targetType,
            Mappings = [new MappingEntry { SourceValue = sourceValue, TargetValue = targetValue }]
        };

        var run = await Execute(config, $$"""{"value":{{sourceJson}},"neighbour":1}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, $$"""{"value":{{sourceJson}},"neighbour":1,"mapped":{{expectedJson}}}""");
        A.CallTo(() => run.Next.Invoke(run.DataContext, run.NodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PathWithoutWildcard_NoMatchingMapping_WritesNothing()
    {
        var run = await Execute(UnitConfig("$.value", "$.mapped"), """{"value":42}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"value":42}""");
    }

    [Fact]
    public async Task PathWithoutWildcard_WithADifferentTargetPath_StillWorks()
    {
        var run = await Execute(UnitConfig("$.value", "$.other.place"), """{"value":2}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"value":2,"other":{"place":"MWh"}}""");
    }

    [Fact]
    public async Task PathWithoutWildcard_ADeepIndexedPath_IsStillASingleValuePath()
    {
        // An index is not a wildcard: this must stay on the single-value route.
        var run = await Execute(UnitConfig("$.rows[1].unit", "$.rows[1].unit"),
            """{"rows":[{"unit":1},{"unit":2}]}""");

        Assert.Null(run.Error);
        AssertDocument(run.DataContext, """{"rows":[{"unit":1},{"unit":"MWh"}]}""");
    }
}

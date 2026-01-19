using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class DataMappingNodeTests
{
    private (IDataContext DataContext, INodeContext NodeContext) PrepareTest(
        DataMappingNodeConfiguration config,
        JToken? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("DataMapping", 0, config, dataContext);

        return (dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_StringMapping_MapsCorrectly()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.status",
            TargetPath = "$.statusCode",
            SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.Int,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = "active", TargetValue = 1, Description = "Active status" },
                new() { SourceValue = "inactive", TargetValue = 0, Description = "Inactive status" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["status"] = "active" });
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("active");

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => Convert.ToInt32(o) == 1),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_IntMapping_MapsCorrectly()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.code",
            TargetPath = "$.description",
            SourceValueType = AttributeValueTypesDto.Int,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = 1, TargetValue = "Success" },
                new() { SourceValue = 2, TargetValue = "Warning" },
                new() { SourceValue = 3, TargetValue = "Error" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["code"] = 2 });
        A.CallTo(() => dataContext.GetSimpleValueByPath<int>(config.Path)).Returns(2);

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => o != null && o.ToString() == "Warning"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoMatchingMapping_CallsNextWithoutSetting()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.status",
            TargetPath = "$.statusCode",
            SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.Int,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = "active", TargetValue = 1 },
                new() { SourceValue = "inactive", TargetValue = 0 }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["status"] = "unknown" });
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("unknown");

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            A<string>._,
            A<object>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<Newtonsoft.Json.JsonSerializer>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullSourceValue_CallsNextWithoutSetting()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.status",
            TargetPath = "$.statusCode",
            SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.Int,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = "active", TargetValue = 1 }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject());
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns(null);

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            A<string>._,
            A<object>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<Newtonsoft.Json.JsonSerializer>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_BooleanMapping_MapsCorrectly()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.isActive",
            TargetPath = "$.status",
            SourceValueType = AttributeValueTypesDto.Boolean,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = true, TargetValue = "Active" },
                new() { SourceValue = false, TargetValue = "Inactive" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["isActive"] = true });
        A.CallTo(() => dataContext.GetSimpleValueByPath<byte>(config.Path)).Returns((byte)1);

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_DoubleMapping_MapsCorrectly()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.value",
            TargetPath = "$.category",
            SourceValueType = AttributeValueTypesDto.Double,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = 1.5, TargetValue = "Low" },
                new() { SourceValue = 2.5, TargetValue = "Medium" },
                new() { SourceValue = 3.5, TargetValue = "High" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["value"] = 2.5 });
        A.CallTo(() => dataContext.GetSimpleValueByPath<double>(config.Path)).Returns(2.5);

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => o != null && o.ToString() == "Medium"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_FirstMatchWins_StopsAfterFirstMatch()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.status",
            TargetPath = "$.result",
            SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = "test", TargetValue = "First Match" },
                new() { SourceValue = "test", TargetValue = "Second Match" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["status"] = "test" });
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("test");

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => o != null && o.ToString() == "First Match"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_Int64Mapping_MapsCorrectly()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.bigNumber",
            TargetPath = "$.description",
            SourceValueType = AttributeValueTypesDto.Int64,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = 9223372036854775807L, TargetValue = "Max Long" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["bigNumber"] = 9223372036854775807L });
        A.CallTo(() => dataContext.GetSimpleValueByPath<long>(config.Path)).Returns(9223372036854775807L);

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => o != null && o.ToString() == "Max Long"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_StringToDoubleMapping_ParsesInvariantCulture()
    {
        var config = new DataMappingNodeConfiguration
        {
            Path = "$.category",
            TargetPath = "$.value",
            SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.Double,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = "Low", TargetValue = "1.5" },
                new() { SourceValue = "High", TargetValue = "3.5" }
            }
        };

        var (dataContext, nodeContext) = PrepareTest(config, new JObject { ["category"] = "Low" });
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("Low");

        var next = A.Fake<NodeDelegate>();
        var node = new DataMappingNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath(
            config.TargetPath,
            A<object>.That.Matches(o => o != null && o.GetType() == typeof(double) && Math.Abs((double)o - 1.5) < 0.001),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            A<Newtonsoft.Json.JsonSerializer>._)).MustHaveHappenedOnceExactly();
    }
}

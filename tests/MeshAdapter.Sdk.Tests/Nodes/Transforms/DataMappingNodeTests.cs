using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class DataMappingNodeTests : NodeTestBase
{
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, "active");

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && Convert.ToInt32(o) == 1),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, 2);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "Warning"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, "unknown");

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            A<string>._,
            A<object?>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustNotHaveHappened();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath<string?>(dataContext, config.Path, null);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            A<string>._,
            A<object?>._,
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustNotHaveHappened();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, true);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "Active"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, 2.5);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "Medium"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, "test");

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "First Match"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, 9223372036854775807L);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "Max Long"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, config.Path, "Low");

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.GetType() == typeof(double) && Math.Abs((double)o - 1.5) < 0.001),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DataMappingNode_BooleanSource_ReadsTrueWithoutThrowing()
    {
        // Pre-migration JToken.Value<byte>() silently coerced JSON true/false to 1/0.
        // STJ's JsonNode.Deserialize<byte>(options) has no boolean→byte converter
        // and throws JsonException at runtime, breaking every pipeline configured
        // with SourceValueType=Boolean.
        // Reading as bool restores parity with the pre-migration behaviour.

        var config = new DataMappingNodeConfiguration
        {
            Path = "$.flag",
            TargetPath = "$.result",
            SourceValueType = AttributeValueTypesDto.Boolean,
            TargetValueType = AttributeValueTypesDto.String,
            Mappings = new List<MappingEntry>
            {
                new() { SourceValue = true, TargetValue = "Yes" },
                new() { SourceValue = false, TargetValue = "No" }
            }
        };

        // Use SetupGetByPath so the mock exercises STJ deserialization via
        // JsonNode.Deserialize<T>(Options), exactly as the production data context does.
        var testData = new System.Text.Json.Nodes.JsonObject
        {
            ["flag"] = true
        };

        var (dataContext, nodeContext, next) = PrepareTest<DataMappingNodeConfiguration>(config, testData);
        SetupGetByPath<bool>(dataContext, config.Path, testData);

        var node = new DataMappingNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set(
            config.TargetPath,
            A<object?>.That.Matches(o => o != null && o.ToString() == "Yes"),
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }
}

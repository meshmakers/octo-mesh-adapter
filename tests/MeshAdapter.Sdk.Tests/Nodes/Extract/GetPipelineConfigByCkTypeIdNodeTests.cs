using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetPipelineConfigByCkTypeIdNodeTests : NodeTestBase
{
    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;

    public GetPipelineConfigByCkTypeIdNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeId_SetsConfigArrayOnDataContext()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeId = "TestModel/TestType",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string> { "{\"key\":\"value1\"}", "{\"key\":\"value2\"}" });

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.configs",
                A<JsonArray?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeIdPath_ResolvesFromDataContext()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeIdPath = "$.ckTypeId",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        SetupGetSimpleValueByPath(dataContext, "$.ckTypeId", "ResolvedModel/ResolvedType");
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("ResolvedModel/ResolvedType"))
            .Returns(new List<string> { "{}" });

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("ResolvedModel/ResolvedType"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoCkTypeIdAndNoPath_Throws()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeIdPathReturningNull_Throws()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeIdPath = "$.ckTypeId",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        SetupGetSimpleValueByPath<string?>(dataContext, "$.ckTypeId", null);

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyResults_SetsEmptyArray()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeId = "TestModel/TestType",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string>());

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.configs",
                A<JsonArray?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_CallsNext()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeId = "TestModel/TestType",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByCkTypeIdNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string>());

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }
}

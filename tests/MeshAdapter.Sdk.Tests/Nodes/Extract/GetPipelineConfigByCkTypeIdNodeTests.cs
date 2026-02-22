using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetPipelineConfigByCkTypeIdNodeTests
{
    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;

    public GetPipelineConfigByCkTypeIdNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        GetPipelineConfigByCkTypeIdNodeConfiguration config, JToken? testData = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            "GetPipelineConfigByCkTypeId",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithCkTypeId_SetsConfigArrayOnDataContext()
    {
        var config = new GetPipelineConfigByCkTypeIdNodeConfiguration
        {
            CkTypeId = "TestModel/TestType",
            TargetPath = "$.configs"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string> { "{\"key\":\"value1\"}", "{\"key\":\"value2\"}" });

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.configs",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JArray>._))
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.ckTypeId")).Returns("ResolvedModel/ResolvedType");
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.ckTypeId")).Returns(null);

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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string>());

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.configs",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JArray>._))
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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId("TestModel/TestType"))
            .Returns(new List<string>());

        var node = new GetPipelineConfigByCkTypeIdNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

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

public class GetPipelineConfigByWellKnownNameNodeTests
{
    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;

    public GetPipelineConfigByWellKnownNameNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        GetPipelineConfigByWellKnownNameNodeConfiguration config, JToken? testData = null)
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
            "GetPipelineConfigByWellKnownName",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithWellKnownName_SetsConfigOnDataContext()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownName = "TestConfig",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.IsDefined("TestConfig")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson("TestConfig")).Returns("{\"key\":\"value\"}");

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.config",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithWellKnownName_CallsNext()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownName = "TestConfig",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.IsDefined("TestConfig")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson("TestConfig")).Returns("{}");

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithWellKnownNamePath_ResolvesFromDataContext()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownNamePath = "$.name",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.name")).Returns("ResolvedConfig");
        A.CallTo(() => _globalConfiguration.IsDefined("ResolvedConfig")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson("ResolvedConfig")).Returns("{}");

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _globalConfiguration.GetRawJson("ResolvedConfig")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUndefinedConfig_Throws()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownName = "NonExistent",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => _globalConfiguration.IsDefined("NonExistent")).Returns(false);

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoNameAndNoPath_Throws()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

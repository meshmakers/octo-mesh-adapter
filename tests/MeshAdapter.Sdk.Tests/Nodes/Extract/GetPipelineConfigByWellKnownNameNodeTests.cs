using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Extracts;
using Meshmakers.Octo.Sdk.Common.Services;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class GetPipelineConfigByWellKnownNameNodeTests : NodeTestBase
{
    private readonly IEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;

    public GetPipelineConfigByWellKnownNameNodeTests()
    {
        _etlContext = A.Fake<IEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithWellKnownName_SetsConfigOnDataContext()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownName = "TestConfig",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByWellKnownNameNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.IsDefined("TestConfig")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson("TestConfig")).Returns("{\"key\":\"value\"}");

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set<JsonNode?>(
                "$.config",
                A<JsonNode?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
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
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByWellKnownNameNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.IsDefined("TestConfig")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson("TestConfig")).Returns("{}");

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithWellKnownNamePath_ResolvesFromDataContext()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            WellKnownNamePath = "$.name",
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByWellKnownNameNodeConfiguration>(config);

        SetupGetSimpleValueByPath(dataContext, "$.name", "ResolvedConfig");
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
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByWellKnownNameNodeConfiguration>(config);

        A.CallTo(() => _globalConfiguration.IsDefined("NonExistent")).Returns(false);

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);

        await Assert.ThrowsAsync<PipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoNameAndNoPath_Throws()
    {
        var config = new GetPipelineConfigByWellKnownNameNodeConfiguration
        {
            TargetPath = "$.config"
        };
        var (dataContext, nodeContext, next) = PrepareTest<GetPipelineConfigByWellKnownNameNodeConfiguration>(config);

        var node = new GetPipelineConfigByWellKnownNameNode(next, _etlContext);

        await Assert.ThrowsAsync<PipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Configuration contracts of <c>LlmQuery@1</c> that are decided before any provider call.
/// </summary>
public class LlmQueryNodeTests : NodeTestBase
{
    private static IMeshEtlContext EtlContextWithoutConfigurations()
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined(A<string>._)).Returns(false);
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => etlContext.TenantId).Returns("tenant-1");
        return etlContext;
    }

    [Theory]
    [InlineData(DataKind.Object)]
    [InlineData(DataKind.Array)]
    public async Task ProcessObjectAsync_DefaultRootPathOnStructuredDocument_FailsAsConfigurationError(DataKind rootKind)
    {
        var config = new LlmQueryNodeConfiguration
        {
            Question = "Summarize.",
            Model = "test-model",
            TargetPath = "$.out"
            // Path stays at its default "$"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$")).Returns(rootKind);
        var node = new LlmQueryNode(next, EtlContextWithoutConfigurations(), A.Fake<IServiceAccountTokenService>());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("whole document", ex.ToString());
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ExplicitPathWithoutContent_WarnsAndContinues()
    {
        var config = new LlmQueryNodeConfiguration
        {
            Question = "Summarize.",
            Model = "test-model",
            Path = "$.body.text",
            TargetPath = "$.out"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.body.text")).Returns(DataKind.Undefined);
        var node = new LlmQueryNode(next, EtlContextWithoutConfigurations(), A.Fake<IServiceAccountTokenService>());

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Unchanged contract for a mistyped or absent explicit path: warn, skip the call, continue.
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

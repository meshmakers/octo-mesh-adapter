using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Configuration contracts of <c>McpToolCall@1</c> that must fail loudly instead of degrading:
/// a non-positive timeout can never work, and a tool called without the arguments the pipeline
/// meant to pass runs unscoped and stores a wrong result as a success.
/// </summary>
public class McpToolCallNodeTests : NodeTestBase
{
    private const string ServerName = "srv";

    private static IMeshEtlContext EtlContextWithServer()
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined(ServerName)).Returns(true);
        A.CallTo(() => globalConfiguration.GetRawJson(ServerName))
            .Returns("""{"url":"https://mcp.example.com/mcp","transport":"Http"}""");

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => etlContext.TenantId).Returns("tenant-1");
        A.CallTo(() => etlContext.TenantRepository).Returns(A.Fake<ITenantRepository>());
        return etlContext;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ProcessObjectAsync_NonPositiveTimeout_FailsAsConfigurationErrorBeforeAnyWork(int timeout)
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = ServerName,
            ToolName = "query_entities",
            TargetPath = "$.result",
            TimeoutSeconds = timeout,
            ContinueOnError = true // must NOT swallow an invalid configuration
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("TimeoutSeconds", ex.ToString());
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ArgumentsNotAJsonObject_FailsInsteadOfCallingWithoutArguments()
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = ServerName,
            ToolName = "query_entities",
            TargetPath = "$.result",
            Arguments = "this is not json"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("not a JSON object", ex.ToString());
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ArgumentsPathResolvesToScalar_ContinueOnErrorSkipsTheCallAndContinues()
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = ServerName,
            ToolName = "query_entities",
            TargetPath = "$.result",
            ArgumentsPath = "$.args",
            ContinueOnError = true
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.args")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<object?>("$.args")).Returns("abc");
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>());

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // ContinueOnError applies to the malformed arguments, but the tool is never called and
        // nothing is written to TargetPath.
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}

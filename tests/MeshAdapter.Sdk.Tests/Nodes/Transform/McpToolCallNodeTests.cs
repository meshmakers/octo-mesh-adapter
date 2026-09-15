using System.IO.Pipes;
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
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Contracts of <c>McpToolCall@1</c>. Configuration errors must fail loudly instead of degrading:
/// a non-positive timeout can never work, an unknown server cannot be called, and a tool called
/// without the arguments the pipeline meant to pass runs unscoped and stores a wrong result as a
/// success. The success path is pinned against an in-process MCP server over a stream transport,
/// so the serialized tool result reaching <c>TargetPath</c> is verified without a network.
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

    [Theory]
    [InlineData("", "query_entities")]
    [InlineData(ServerName, " ")]
    public async Task ProcessObjectAsync_RequiredNameMissing_FailsEvenWithContinueOnError(string mcpName, string toolName)
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = mcpName,
            ToolName = toolName,
            TargetPath = "$.result",
            ContinueOnError = true // invalid persisted configuration must not become a silent pass-through
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>());

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownMcpConfiguration_FailsEvenWithContinueOnError()
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = "does-not-exist",
            ToolName = "some_tool",
            TargetPath = "$.result",
            ContinueOnError = true // an unresolvable server must not become a silent pass-through
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>());

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("does-not-exist", ex.Message);
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
    public async Task ProcessObjectAsync_ArgumentsPathResolvesToNothing_FailsInsteadOfCallingWithoutArguments()
    {
        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = ServerName,
            ToolName = "query_entities",
            TargetPath = "$.result",
            ArgumentsPath = "$.args"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.args")).Returns(DataKind.Undefined);
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>())
        {
            // Would only be reached if the node wrongly proceeded to the call.
            TransportFactory = _ => throw new InvalidOperationException("the tool must not be called")
        };

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("$.args", ex.Message);
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

    [Fact]
    public async Task ProcessObjectAsync_ToolCallSucceeds_WritesTheSerializedResultToTargetPathAndContinues()
    {
        // Arrange: an in-process MCP server exposing one tool, wired to the node over anonymous pipes.
        var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        await using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);
        await using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.ClientSafePipeHandle);

        var tools = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create((string query) => $"echo:{query}",
                new McpServerToolCreateOptions { Name = "echo", Description = "Echoes its query argument." })
        };
        await using var server = McpServer.Create(
            new StreamServerTransport(serverInput, serverToClient, "test-server"),
            new McpServerOptions { ToolCollection = tools });
        using var serverCts = new CancellationTokenSource();
        var serverRun = server.RunAsync(serverCts.Token);

        var config = new McpToolCallNodeConfiguration
        {
            McpConfigurationName = ServerName,
            ToolName = "echo",
            Arguments = """{"query":"hello"}""",
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        JsonNode? written = null;
        A.CallTo(() => dataContext.Set("$.result", A<JsonNode?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes(call => written = call.GetArgument<JsonNode?>(1));
        var node = new McpToolCallNode(next, EtlContextWithServer(), A.Fake<IServiceAccountTokenService>())
        {
            TransportFactory = _ => new StreamClientTransport(clientToServer, clientInput)
        };

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert: the whole CallToolResult (content blocks + isError) is what lands at TargetPath.
        Assert.NotNull(written);
        var content = Assert.IsType<JsonArray>(written!["content"]);
        var text = Assert.IsType<JsonObject>(Assert.Single(content));
        Assert.Equal("text", text["type"]?.GetValue<string>());
        Assert.Equal("echo:hello", text["text"]?.GetValue<string>());
        Assert.NotEqual(true, written["isError"]?.GetValue<bool?>());
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        // Observe the server task so a fault on the server side fails the test instead of vanishing:
        // McpServer.DisposeAsync does not await RunAsync. Cancellation is the expected outcome.
        serverCts.Cancel();
        try
        {
            await serverRun.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // expected: RunAsync ended because we cancelled it
        }
    }
}

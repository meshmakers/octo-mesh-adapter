using System.ComponentModel;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
///     <c>McpToolCall@1</c> (AB#8658) against a real MCP server: the ModelContextProtocol.AspNetCore SDK
///     that octo-mcp-service uses, hosted in process (stateful Streamable HTTP, sessions, SSE answers) behind
///     a bearer check. Proves the handshake, the identity hand-over, the result and both MCP error shapes
///     end to end — without a cluster or a paid call.
/// </summary>
public sealed class McpToolCallNodeIntegrationTests : IAsyncLifetime
{
    private const string Token = "curator-test-token";
    private const string Tenant = "tenant1";

    private WebApplication? _app;
    private int _mcpRequests;

    [McpServerToolType]
    public sealed class KnowledgeTestTools
    {
        [McpServerTool(Name = "knowledge_upsert_page"), Description("Test double of the knowledge write tool.")]
        public static string UpsertPage(string title, string markdown) =>
            new JsonObject { ["title"] = title, ["length"] = markdown.Length, ["version"] = 1 }.ToJsonString();

        [McpServerTool(Name = "knowledge_guarded"), Description("Always refused by the content guard.")]
        public static string Guarded(string title) =>
            throw new McpException("Content guard: the page looks like it contains a credential.");
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<KnowledgeTestTools>();

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _mcpRequests);
            if (context.Request.Headers.Authorization != $"Bearer {Token}")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        _app.MapMcp("/{tenantId}/mcp");
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
        }
    }

    private (McpToolCallNode Node, IDataContext DataContext, INodeContext NodeContext) Prepare(
        McpToolCallNodeConfiguration config, string token = Token)
    {
        var server = _app!.GetTestServer();
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(server.CreateHandler()));

        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, "knowledge-curator",
            A<CancellationToken>._)).Returns(token);

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns(Tenant);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetKind("$.args")).Returns(DataKind.Object);
        A.CallTo(() => dataContext.Get<JsonNode>("$.args"))
            .Returns(new JsonObject { ["title"] = "Netzlaufwerk", ["markdown"] = "# Netzlaufwerk" });

        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<McpToolCallNodeConfiguration>()).Returns(config);
        A.CallTo(() => nodeContext.NodePath).Returns("/McpToolCall@1");

        return (new McpToolCallNode(A.Fake<NodeDelegate>(), etlContext, factory, tokenService), dataContext,
            nodeContext);
    }

    private static McpToolCallNodeConfiguration Config(string tool) => new()
    {
        AllowedTools = ["knowledge_upsert_page", "knowledge_guarded", "knowledge_missing"],
        ToolName = tool,
        ArgumentsPath = "$.args",
        McpServerUrl = "http://localhost",
        McpServiceAccountConfigName = "knowledge-curator",
        TargetPath = "$.result"
    };

    private static object? Written(IDataContext dataContext) =>
        Fake.GetCalls(dataContext).Single(c => c.Method.Name == nameof(IDataContext.Set)).Arguments[1];

    [Fact]
    public async Task AllowlistedTool_RunsAgainstARealMcpServer_AndTheStructuredResultIsWritten()
    {
        var (node, dataContext, nodeContext) = Prepare(Config("knowledge_upsert_page"));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = Assert.IsAssignableFrom<JsonObject>(Written(dataContext));
        Assert.Equal("Netzlaufwerk", result["title"]!.GetValue<string>());
        Assert.Equal("# Netzlaufwerk".Length, result["length"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolErrorResult_FailsWithTheServersMessage()
    {
        var (node, dataContext, nodeContext) = Prepare(Config("knowledge_guarded"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("MCP tool 'knowledge_guarded' returned an error", ex.Message);
        Assert.Contains("looks like it contains a credential", ex.Message);
    }

    [Fact]
    public async Task ProtocolError_FailsWithTheMcpErrorCodeAndMessage()
    {
        // Allowlisted, but the server does not have it: the SDK answers with a JSON-RPC error.
        var (node, dataContext, nodeContext) = Prepare(Config("knowledge_missing"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("MCP error -32602", ex.Message);
        Assert.Contains("knowledge_missing", ex.Message);
    }

    [Fact]
    public async Task ToolOutsideTheAllowlist_NeverReachesTheServer()
    {
        var (node, dataContext, nodeContext) = Prepare(Config("knowledge_upsert_page") with { ToolName = "delete_entities" });

        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(0, _mcpRequests);
    }

    [Fact]
    public async Task WrongIdentity_IsRejected_AndTheTokenIsNotInTheError()
    {
        const string wrongToken = "some-other-accounts-token";
        var (node, dataContext, nodeContext) = Prepare(Config("knowledge_upsert_page"), wrongToken);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("HTTP 401", ex.Message);
        Assert.DoesNotContain(wrongToken, ex.ToString());
    }
}

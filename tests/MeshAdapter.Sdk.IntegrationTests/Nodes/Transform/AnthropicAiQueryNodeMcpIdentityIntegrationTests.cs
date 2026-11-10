using System.ComponentModel;
using System.Net;
using System.Text;
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
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
///     AB#8685 against a real MCP server: the ModelContextProtocol.AspNetCore SDK that octo-mcp-service uses,
///     hosted in process behind a bearer check that answers 401 like the real service (AB#4315). Without an
///     identity <c>AnthropicAiQuery@1</c> must not reach the server at all; with a service account it loads
///     the tools as before. The Anthropic Messages API is answered by a local stub — no paid call.
/// </summary>
public sealed class AnthropicAiQueryNodeMcpIdentityIntegrationTests : IAsyncLifetime
{
    private const string Token = "service-account-test-token";
    private const string Tenant = "tenant1";
    private const string AnthropicHost = "api.anthropic.com";

    private WebApplication? _app;
    private int _mcpRequests;
    private int _mcpUnauthorized;

    [McpServerToolType]
    public sealed class QueryTestTools
    {
        [McpServerTool(Name = "query_entities_simple"), Description("Test double of an MCP query tool.")]
        public static string QueryEntities(string ckTypeId) => "[]";
    }

    /// <summary>Sends Anthropic requests to a stub and everything else to the in-process MCP server.</summary>
    private sealed class RoutingHandler(HttpMessageHandler mcpHandler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _mcp = new(mcpHandler);
        public List<JsonNode> AnthropicBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != AnthropicHost)
            {
                return await _mcp.SendAsync(request, cancellationToken);
            }

            AnthropicBodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"stop_reason":"end_turn","content":[{"type":"text","text":"the answer"}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<QueryTestTools>();

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _mcpRequests);
            if (context.Request.Headers.Authorization != $"Bearer {Token}")
            {
                Interlocked.Increment(ref _mcpUnauthorized);
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

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, RoutingHandler Http)
        Prepare(AnthropicAiQueryNodeConfiguration config)
    {
        var http = new RoutingHandler(_app!.GetTestServer().CreateHandler());
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(http, false));

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns(Tenant);
        A.CallTo(() => etlContext.TenantRepository).Returns(A.Fake<ITenantRepository>());
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());

        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, A<string>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(Token));

        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("some content");

        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<AnthropicAiQueryNodeConfiguration>()).Returns(config);
        A.CallTo(() => nodeContext.NodePath).Returns("/AnthropicAiQuery@1");

        var node = new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory,
            tokenService);
        return (node, dataContext, nodeContext, http);
    }

    private static AnthropicAiQueryNodeConfiguration Config(string? serviceAccount) => new()
    {
        ApiKey = "test-key",
        Model = "claude-sonnet-4-6",
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        ResponseFormat = "text",
        McpServerUrl = "http://localhost",
        McpServiceAccountConfigName = serviceAccount
    };

    [Fact]
    public async Task WithoutIdentity_TheMcpServerIsNeverCalled_AndTheQueryRunsWithoutTools()
    {
        var (node, dataContext, nodeContext, http) = Prepare(Config(serviceAccount: null));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(0, _mcpRequests);
        Assert.Null(Assert.Single(http.AnthropicBodies)["tools"]);
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains("skips MCP"), A<object[]>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WithServiceAccount_TheToolsAreLoadedFromTheMcpServer()
    {
        var (node, dataContext, nodeContext, http) = Prepare(Config(serviceAccount: "ai-service-account"));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.True(_mcpRequests > 0);
        Assert.Equal(0, _mcpUnauthorized);
        var tools = Assert.IsType<JsonArray>(Assert.Single(http.AnthropicBodies)["tools"]);
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "query_entities_simple");
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains("skips MCP"), A<object[]>._))
            .MustNotHaveHappened();
    }
}

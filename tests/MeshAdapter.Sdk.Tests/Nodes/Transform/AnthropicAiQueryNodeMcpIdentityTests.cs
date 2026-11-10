using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     AB#8685: <c>AnthropicAiQuery@1</c> never calls an MCP server without credentials. An MCP URL — set on
///     the node or inherited from the AiConfiguration's <c>McpServerUrl</c> — without
///     <c>mcpServiceAccountConfigName</c> or delegation to the caller is skipped with one warning; the query
///     runs without tools. With an identity the MCP path is unchanged. The Anthropic API and the MCP server
///     are mocked; no paid call is made.
/// </summary>
public class AnthropicAiQueryNodeMcpIdentityTests : NodeTestBase
{
    private const string McpBaseUrl = "https://mcp.example.com";
    private const string AnthropicHost = "api.anthropic.com";
    private const string AiConfigurationName = "AiConfig";
    private const string ServiceAccountConfig = "ServiceAccountConfig";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();
    private readonly RecordingHandler _http = new();

    public AnthropicAiQueryNodeMcpIdentityTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    /// <summary>
    ///     Answers the Anthropic API with a plain text answer and the MCP server with an empty tool list,
    ///     recording every request URI and its bearer token.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string? Bearer)> Requests { get; } = [];

        public IEnumerable<Uri> McpRequests => Requests.Select(r => r.Uri).Where(u => u.Host != AnthropicHost);

        public IEnumerable<JsonNode> AnthropicBodies { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            string body;
            if (request.RequestUri!.Host == AnthropicHost)
            {
                AnthropicBodies = AnthropicBodies
                    .Append(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!).ToList();
                body = """{"stop_reason":"end_turn","content":[{"type":"text","text":"the answer"}]}""";
            }
            else
            {
                body = """{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, IPipelineLogger Logger)
        Prepare(AnthropicAiQueryNodeConfiguration config)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_http, false));

        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("some content");

        var node = new AnthropicAiQueryNode(next, _etlContext, factory, _tokenService);
        return (node, dataContext, nodeContext, logger);
    }

    private static AnthropicAiQueryNodeConfiguration Config(string? mcpServerUrl = McpBaseUrl) => new()
    {
        ApiKey = "test-key",
        Model = "claude-sonnet-4-6",
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        ResponseFormat = "text",
        McpServerUrl = mcpServerUrl
    };

    private static IEnumerable<string> Warnings(IPipelineLogger logger) =>
        Fake.GetCalls(logger)
            .Where(c => c.Method.Name == nameof(IPipelineLogger.Warning))
            .Select(c => (string)c.Arguments[2]!);

    // ---- AC1: MCP URL present, no identity configured ----

    [Fact]
    public async Task NodeMcpUrlWithoutIdentity_MakesNoMcpCall_WarnsOnce_AndQueriesWithoutTools()
    {
        var (node, dataContext, nodeContext, logger) = Prepare(Config());

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(_http.McpRequests);
        var anthropicBody = Assert.Single(_http.AnthropicBodies);
        Assert.Null(anthropicBody["tools"]);

        var warning = Assert.Single(Warnings(logger));
        Assert.Contains(nodeContext.NodePath.ToString(), warning);
        Assert.Contains("mcpServiceAccountConfigName", warning);
        Assert.Contains("mcpDelegateToCaller", warning);
        Assert.Contains("without tools", warning);

        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        Assert.Single(new DataContextWrites(dataContext).All, w => w.Path == "$.out");
    }

    [Fact]
    public async Task InheritedAiConfigurationMcpUrlWithoutIdentity_MakesNoMcpCall_AndWarnsOnce()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(AiConfigurationName)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson(AiConfigurationName))
            .Returns($$"""{"apiKey":"test-key","mcpServerUrl":"{{McpBaseUrl}}"}""");
        var config = Config(mcpServerUrl: null);
        config.ApiKey = null;
        config.ApiKeyConfigurationName = AiConfigurationName;
        var (node, dataContext, nodeContext, logger) = Prepare(config);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(_http.McpRequests);
        Assert.Single(_http.AnthropicBodies);
        Assert.Contains("mcpServiceAccountConfigName", Assert.Single(Warnings(logger)));
    }

    [Fact]
    public async Task NoMcpUrlAndNoIdentity_StaysSilent()
    {
        var (node, dataContext, nodeContext, logger) = Prepare(Config(mcpServerUrl: null));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(_http.McpRequests);
        Assert.Empty(Warnings(logger));
    }

    // ---- AC2: identity configured — behaviour unchanged ----

    [Fact]
    public async Task ServiceAccountIdentity_CallsMcpWithTheServiceAccountToken()
    {
        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(_tenantRepository, ServiceAccountConfig,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("service-account-token"));
        var config = Config();
        config.McpServiceAccountConfigName = ServiceAccountConfig;
        var (node, dataContext, nodeContext, logger) = Prepare(config);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(_tenantRepository, ServiceAccountConfig,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        Assert.NotEmpty(_http.McpRequests);
        Assert.All(_http.Requests.Where(r => r.Uri.Host != AnthropicHost),
            r => Assert.Equal("service-account-token", r.Bearer));
        Assert.All(_http.McpRequests, u => Assert.Equal($"{McpBaseUrl}/testTenant/mcp", u.ToString()));
        Assert.DoesNotContain(Warnings(logger), w => w.Contains("skips MCP"));
    }

    [Fact]
    public async Task DelegateToCaller_CallsMcpWithTheDelegatedToken()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns("caller-token");
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                "caller-token", A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("delegated-token"));
        var config = Config();
        config.McpServiceAccountConfigName = ServiceAccountConfig;
        config.McpDelegateToCaller = true;
        var (node, dataContext, nodeContext, logger) = Prepare(config);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotEmpty(_http.McpRequests);
        Assert.All(_http.Requests.Where(r => r.Uri.Host != AnthropicHost),
            r => Assert.Equal("delegated-token", r.Bearer));
        Assert.DoesNotContain(Warnings(logger), w => w.Contains("skips MCP"));
    }

    [Fact]
    public async Task DelegateToCallerWithoutServiceAccount_StillFailsClosed()
    {
        // Delegation counts as an identity: the node does not skip MCP silently but keeps its
        // fail-closed contract (AB#5031) — a misconfigured delegation is an error, not a warning.
        A.CallTo(() => _etlContext.CallerAccessToken).Returns("caller-token");
        var config = Config();
        config.Identity = NodeExecutionIdentity.Caller;
        var (node, dataContext, nodeContext, _) = Prepare(config);

        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Empty(_http.McpRequests);
    }

    [Theory]
    [InlineData(null, false, NodeExecutionIdentity.ServiceAccount, false)]
    [InlineData("", false, NodeExecutionIdentity.ServiceAccount, false)]
    [InlineData(null, false, NodeExecutionIdentity.System, false)]
    [InlineData(ServiceAccountConfig, false, NodeExecutionIdentity.ServiceAccount, true)]
    [InlineData(null, true, NodeExecutionIdentity.ServiceAccount, true)]
    [InlineData(null, false, NodeExecutionIdentity.Caller, true)]
    public void HasMcpIdentity_RequiresAServiceAccountOrDelegation(string? serviceAccount, bool delegateToCaller,
        NodeExecutionIdentity identity, bool expected)
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            McpServiceAccountConfigName = serviceAccount,
            McpDelegateToCaller = delegateToCaller,
            Identity = identity
        };

        Assert.Equal(expected, AnthropicAiQueryNode.HasMcpIdentity(config));
    }
}

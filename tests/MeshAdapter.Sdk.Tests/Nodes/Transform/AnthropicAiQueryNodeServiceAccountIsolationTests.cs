using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     AB#8699: <c>AnthropicAiQuery@1</c> authenticates MCP with the token of exactly the service account its
///     <c>mcpServiceAccountConfigName</c> names — never with another account's token and never with the
///     adapter's own identity — and a service-account token never lands in the process-wide
///     <see cref="IServiceClientAccessToken" /> (the adapter's identity towards the controller).
/// </summary>
/// <remarks>
///     These tests wire the REAL <see cref="ServiceAccountTokenService" /> and a REAL
///     <see cref="ServiceClientAccessToken" /> holder, both shared by several node instances the way the DI
///     singletons are shared by concurrent pipelines of one adapter process. Only the identity endpoint, the
///     MCP server and the Anthropic API are scripted. Cases from Ariadne's assessment (AB#8699, comment
///     6655174): (a) two service accounts in one adapter, (b) the adapter's own token refresh while a
///     service-account token is valid, (c) a configuration naming a foreign tenant.
/// </remarks>
public class AnthropicAiQueryNodeServiceAccountIsolationTests : NodeTestBase
{
    private const string McpBaseUrl = "https://mcp.example.com";
    private const string AnthropicHost = "api.anthropic.com";
    private const string IdentityHost = "identity.example.com";
    private const string AdapterTenant = "testTenant";
    private const string AdapterOwnToken = "adapter-own-identity-token";

    private static readonly RtCkId<CkTypeId> ServiceAccountType =
        new("System.Communication/ServiceAccountConfiguration");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceClientAccessToken _adapterIdentity = new ServiceClientAccessToken();
    private readonly ScriptedEndpoints _http = new();
    private readonly Dictionary<string, RtEntity> _configurations = new(StringComparer.Ordinal);
    private readonly ServiceAccountTokenService _tokenService;

    public AnthropicAiQueryNodeServiceAccountIsolationTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns(AdapterTenant);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());
        A.CallTo(() => _tenantRepository.TenantId).Returns(AdapterTenant);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(A.Fake<IOctoSession>()));
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, ServiceAccountType, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> _, RtEntityQueryOptions options, int? _, int? _) =>
                Task.FromResult(ResultFor(options)));

        AddConfiguration("AccountA", "client-a");
        AddConfiguration("AccountB", "client-b");

        _tokenService = new ServiceAccountTokenService(NullLogger<ServiceAccountTokenService>.Instance,
            new HttpClient(_http, disposeHandler: false));
    }

    private void AddConfiguration(string wellKnownName, string clientId, string? tenantId = AdapterTenant)
    {
        var entity = new RtEntity(ServiceAccountType,
            new OctoObjectId($"6700000000000000000000{_configurations.Count + 10:x2}"));
        entity.SetAttributeRawValue("IssuerUri", $"https://{IdentityHost}");
        entity.SetAttributeRawValue("ClientId", clientId);
        entity.SetAttributeRawValue("ClientSecret", $"secret-of-{clientId}");
        entity.SetAttributeRawValue("TenantId", tenantId);
        _configurations[wellKnownName] = entity;
    }

    private IResultSet<RtEntity> ResultFor(RtEntityQueryOptions options)
    {
        var name = options.FieldFilters?
            .FirstOrDefault(f => f.AttributePath == "rtWellKnownName")?.ComparisonValue as string;
        var items = name != null && _configurations.TryGetValue(name, out var entity)
            ? new List<RtEntity> { entity }
            : new List<RtEntity>();
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(items);
        A.CallTo(() => resultSet.TotalCount).Returns(items.Count);
        return resultSet;
    }

    /// <summary>
    ///     Identity endpoint (discovery + token, one distinct token per client and tenant), MCP server (empty
    ///     tool list) and Anthropic API (plain text answer). Records the bearer of every MCP request.
    /// </summary>
    private sealed class ScriptedEndpoints : HttpMessageHandler
    {
        public List<string?> McpBearers { get; } = [];

        public int AnthropicCalls { get; private set; }

        public JsonNode? LastAnthropicBody { get; private set; }

        public int TokenRequests { get; private set; }

        /// <summary>When set, the token endpoint refuses every request.</summary>
        public bool RefuseTokens { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == AnthropicHost)
            {
                AnthropicCalls++;
                LastAnthropicBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json("""{"stop_reason":"end_turn","content":[{"type":"text","text":"the answer"}]}""");
            }

            if (uri.Host == IdentityHost)
            {
                if (uri.AbsolutePath.EndsWith("/jwks", StringComparison.Ordinal))
                {
                    return Json("""{"keys":[]}""");
                }

                if (uri.AbsolutePath.Contains("/.well-known/openid-configuration", StringComparison.Ordinal))
                {
                    return Json($$"""
                                  {
                                    "issuer": "https://{{IdentityHost}}",
                                    "token_endpoint": "https://{{IdentityHost}}/connect/token",
                                    "jwks_uri": "https://{{IdentityHost}}/.well-known/openid-configuration/jwks"
                                  }
                                  """);
                }

                TokenRequests++;
                if (RefuseTokens)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_client"}""", Encoding.UTF8,
                            "application/json")
                    };
                }

                var basic = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
                var clientId = Uri.UnescapeDataString(basic.Split(':')[0]);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var tenant = form.Split('&')
                    .Select(p => p.Split('=', 2))
                    .Where(p => p[0] == "acr_values")
                    .Select(p => Uri.UnescapeDataString(p[1]))
                    .Single();
                return Json(
                    $$"""{"access_token":"{{TokenFor(clientId, tenant)}}","token_type":"Bearer","expires_in":3600}""");
            }

            McpBearers.Add(request.Headers.Authorization?.Parameter);
            return Json("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string TokenFor(string clientId, string acrTenant) => $"sa-token|{clientId}|{acrTenant}";

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, IPipelineLogger Logger)
        Prepare(string serviceAccountConfigName)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_http, false));

        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-4-6",
            Question = "What is it about?",
            Path = "$.text",
            TargetPath = "$.out",
            ResponseFormat = "text",
            McpServerUrl = McpBaseUrl,
            McpServiceAccountConfigName = serviceAccountConfigName
        };

        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("some content");

        var node = new AnthropicAiQueryNode(next, _etlContext, factory, _tokenService);
        return (node, dataContext, nodeContext, logger);
    }

    private async Task<(IReadOnlyList<string?> McpBearers, IPipelineLogger Logger, IDataContext DataContext)>
        RunAsync(string serviceAccountConfigName)
    {
        var before = _http.McpBearers.Count;
        var (node, dataContext, nodeContext, logger) = Prepare(serviceAccountConfigName);
        await node.ProcessObjectAsync(dataContext, nodeContext);
        return (_http.McpBearers.Skip(before).ToList(), logger, dataContext);
    }

    private static IEnumerable<string> Warnings(IPipelineLogger logger) =>
        Fake.GetCalls(logger)
            .Where(c => c.Method.Name == nameof(IPipelineLogger.Warning))
            .Select(c => (string)c.Arguments[2]!);

    [Fact]
    public async Task CaseA_TwoNodesWithTwoServiceAccounts_EachCallsMcpWithItsOwnAccountsToken()
    {
        var a = await RunAsync("AccountA");
        var b = await RunAsync("AccountB");

        Assert.NotEmpty(a.McpBearers);
        Assert.NotEmpty(b.McpBearers);
        Assert.All(a.McpBearers, bearer => Assert.Equal(TokenFor("client-a", $"tenant:{AdapterTenant}"), bearer));
        Assert.All(b.McpBearers, bearer => Assert.Equal(TokenFor("client-b", $"tenant:{AdapterTenant}"), bearer));
    }

    [Fact]
    public async Task CaseA_InterleavedRuns_NeverHandOneAccountAnotherAccountsToken()
    {
        var a1 = await RunAsync("AccountA");
        var b1 = await RunAsync("AccountB");
        var a2 = await RunAsync("AccountA");

        Assert.All(a1.McpBearers.Concat(a2.McpBearers),
            bearer => Assert.Equal(TokenFor("client-a", $"tenant:{AdapterTenant}"), bearer));
        Assert.All(b1.McpBearers, bearer => Assert.Equal(TokenFor("client-b", $"tenant:{AdapterTenant}"), bearer));
        // Cached per (tenant, client, issuer): one token request per account, not per run.
        Assert.Equal(2, _http.TokenRequests);
    }

    [Fact]
    public async Task CaseB_AdapterTokenRefreshWhileTheServiceAccountTokenIsValid_McpKeepsTheServiceAccountToken()
    {
        await RunAsync("AccountA");

        // The adapter's own AdapterAccessTokenService refreshes its identity into the shared holder.
        _adapterIdentity.AccessToken = AdapterOwnToken;

        var second = await RunAsync("AccountA");

        Assert.NotEmpty(second.McpBearers);
        Assert.All(second.McpBearers,
            bearer => Assert.Equal(TokenFor("client-a", $"tenant:{AdapterTenant}"), bearer));
        Assert.Equal(AdapterOwnToken, _adapterIdentity.AccessToken);
    }

    [Fact]
    public async Task CaseB_AServiceAccountTokenNeverOverwritesTheAdaptersOwnIdentity()
    {
        _adapterIdentity.AccessToken = AdapterOwnToken;

        var run = await RunAsync("AccountA");

        Assert.All(run.McpBearers, bearer => Assert.Equal(TokenFor("client-a", $"tenant:{AdapterTenant}"), bearer));
        Assert.Equal(AdapterOwnToken, _adapterIdentity.AccessToken);
    }

    [Fact]
    public async Task CaseC_AForeignTenantConfiguration_IsKeyedByTenant_AndNeverReachesTheAdapterIdentity()
    {
        // Same client id as AccountA, different tenant: a different identity, so a different token.
        AddConfiguration("AccountForeign", "client-a", tenantId: "other-tenant");
        _adapterIdentity.AccessToken = AdapterOwnToken;

        var own = await RunAsync("AccountA");
        var foreign = await RunAsync("AccountForeign");

        Assert.All(own.McpBearers, bearer => Assert.Equal(TokenFor("client-a", $"tenant:{AdapterTenant}"), bearer));
        Assert.All(foreign.McpBearers, bearer => Assert.Equal(TokenFor("client-a", "tenant:other-tenant"), bearer));
        Assert.Equal(AdapterOwnToken, _adapterIdentity.AccessToken);
    }

    [Fact]
    public async Task FailClosed_AConfiguredServiceAccountWithoutAToken_MakesNoMcpCall_WarnsAndQueriesWithoutTools()
    {
        _http.RefuseTokens = true;
        _adapterIdentity.AccessToken = AdapterOwnToken;

        var run = await RunAsync("AccountA");

        // No MCP call at all — neither unauthenticated nor with the adapter's own token lying in the holder.
        Assert.Empty(run.McpBearers);
        Assert.Equal(1, _http.AnthropicCalls);
        Assert.Null(_http.LastAnthropicBody!["tools"]);
        var warning = Assert.Single(Warnings(run.Logger), w => w.Contains("AccountA"));
        Assert.Contains("no MCP call", warning);
        Assert.Single(new DataContextWrites(run.DataContext).All, w => w.Path == "$.out");
    }

    [Fact]
    public async Task FailClosed_AMissingServiceAccountConfiguration_MakesNoMcpCall()
    {
        var run = await RunAsync("DoesNotExist");

        Assert.Empty(run.McpBearers);
        Assert.Equal(1, _http.AnthropicCalls);
        Assert.Contains(Warnings(run.Logger), w => w.Contains("DoesNotExist") && w.Contains("no MCP call"));
    }

    [Fact]
    public void TheNodeCannotReachTheAdaptersProcessWideTokenHolder()
    {
        // Structural half of case (b): the node has no access to the holder it used to read the MCP token from.
        Assert.DoesNotContain(typeof(AnthropicAiQueryNode).GetConstructors().Single().GetParameters(),
            p => typeof(IServiceClientAccessToken).IsAssignableFrom(p.ParameterType));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The tokens <c>AnthropicAiQuery@1</c> handles for its MCP calls never reach a diagnostic output.
///     <para>
///         Invariant: every token the node holds — the service account's token, the delegated token of
///         the calling user and the caller's own access token it exchanges for it — is registered as a
///         secret of the execution <b>before</b> the node writes a log line or makes a call that can fail.
///         The execution log, error messages, debug snapshots and the persisted result then show the mask.
///     </para>
///     <para>
///         Abuse cases, one test each: an MCP server that answers 401 and echoes the bearer it received
///         (the node logs that body), for the delegated and for the service-account token; an identity
///         service whose refusal quotes the subject token, with the error logged and with the error thrown.
///     </para>
///     The node runs with a real node context, so the registry and the redaction are the pipeline's own.
/// </summary>
public class AnthropicAiQueryNodeMcpTokenMaskingTests
{
    private const string ServiceAccountConfig = "ServiceAccountConfig";
    private const string CallerToken = "test-token-caller-eyJhbGciOiJub25lIn0.caller";
    private const string DelegatedToken = "test-token-delegated-eyJhbGciOiJub25lIn0.delegated";
    private const string ServiceAccountToken = "test-token-service-account-eyJhbGciOiJub25lIn0.sa";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();
    private readonly IPipelineLogger _logger = A.Fake<IPipelineLogger>();
    private readonly EchoingMcpServer _http = new();

    public AnthropicAiQueryNodeMcpTokenMaskingTests()
    {
        A.CallTo(() => _etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());
    }

    /// <summary>
    ///     Answers every MCP request with 401 and a body that quotes the Authorization header it received,
    ///     as a gateway or a verbose identity middleware does; the Anthropic API answers normally.
    /// </summary>
    private sealed class EchoingMcpServer : HttpMessageHandler
    {
        public int McpRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.anthropic.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"stop_reason":"end_turn","content":[{"type":"text","text":"the answer"}]}""",
                        Encoding.UTF8, "application/json")
                });
            }

            McpRequests++;
            var body = new JsonObject
            {
                ["error"] = "invalid_token",
                ["error_description"] = $"The credential '{request.Headers.Authorization}' was rejected."
            }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private (AnthropicAiQueryNode Node, DataContextImpl DataContext, INodeContext NodeContext) Prepare(
        AnthropicAiQueryNodeConfiguration config)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_http, false));

        var dataContext = new DataContextImpl(JsonDocument.Parse("""{"text":"some content"}"""));
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                _logger, dataContext)
            .RegisterChildNode("AnthropicAiQueryNode", 0, config, dataContext);

        var node = new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), _etlContext, factory, _tokenService);
        return (node, dataContext, nodeContext);
    }

    private static AnthropicAiQueryNodeConfiguration Config(bool delegateToCaller, bool continueOnError = false) =>
        new()
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-4-6",
            Question = "What is it about?",
            Path = "$.text",
            TargetPath = "$.out",
            ResponseFormat = "text",
            McpServerUrl = "https://mcp.example.test",
            McpServiceAccountConfigName = ServiceAccountConfig,
            McpDelegateToCaller = delegateToCaller,
            ContinueOnError = continueOnError
        };

    /// <summary>Everything the node handed to the pipeline logger: messages, arguments and exceptions.</summary>
    private string EverythingLogged()
    {
        var text = new StringBuilder();
        foreach (var call in Fake.GetCalls(_logger))
        {
            foreach (var argument in call.Arguments)
            {
                switch (argument)
                {
                    case object[] values:
                        foreach (var value in values)
                        {
                            text.AppendLine(value?.ToString());
                        }

                        break;
                    default:
                        text.AppendLine(argument?.ToString());
                        break;
                }
            }
        }

        return text.ToString();
    }

    private static void AssertMasked(INodeContext nodeContext, string token)
    {
        Assert.NotNull(nodeContext.SecretRegistry);
        Assert.Equal(PipelineSecretRegistry.Mask, nodeContext.SecretRegistry.Redact(token));
    }

    [Fact]
    public async Task DelegationMode_AnMcpServerEchoingTheBearer_LeaksNeitherTheDelegatedNorTheCallersToken()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(DelegatedToken));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: true));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The scenario is real: MCP was called with the delegated token and its 401 body was logged.
        Assert.True(_http.McpRequests > 0);
        var logged = EverythingLogged();
        Assert.Contains("MCP initialize failed", logged);
        Assert.DoesNotContain(DelegatedToken, logged);
        Assert.DoesNotContain(CallerToken, logged);

        // Debug snapshots and the persisted result are masked from the same registry.
        AssertMasked(nodeContext, DelegatedToken);
        AssertMasked(nodeContext, CallerToken);

        var result = dataContext.Get<JsonNode>("$")!.ToJsonString();
        Assert.Contains("the answer", result);
        Assert.DoesNotContain(DelegatedToken, result);
        Assert.DoesNotContain(CallerToken, result);
    }

    [Fact]
    public async Task DelegationMode_ARefusalQuotingTheCallersToken_IsLoggedMasked()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException($"invalid_grant: subject_token '{CallerToken}' is expired"));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: true, continueOnError: true));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var logged = EverythingLogged();
        Assert.Contains("invalid_grant", logged);
        Assert.DoesNotContain(CallerToken, logged);
        AssertMasked(nodeContext, CallerToken);
        Assert.Equal(0, _http.McpRequests);
    }

    [Fact]
    public async Task DelegationMode_ARefusalQuotingTheCallersToken_IsMaskedInTheErrorOfTheExecution()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException($"invalid_grant: subject_token '{CallerToken}' is expired"));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: true));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        // The pipeline records the error through the registry of the execution.
        var recorded = nodeContext.SecretRegistry!.RedactException(thrown).ToString();
        Assert.Contains("invalid_grant", recorded);
        Assert.DoesNotContain(CallerToken, recorded);
        Assert.Equal(0, _http.McpRequests);
    }

    [Fact]
    public async Task ServiceAccountMode_AnMcpServerEchoingTheBearer_DoesNotLeakTheToken()
    {
        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(_tenantRepository, ServiceAccountConfig,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(ServiceAccountToken));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: false));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.True(_http.McpRequests > 0);
        var logged = EverythingLogged();
        Assert.Contains("MCP initialize failed", logged);
        Assert.DoesNotContain(ServiceAccountToken, logged);
        AssertMasked(nodeContext, ServiceAccountToken);
        Assert.DoesNotContain(ServiceAccountToken, dataContext.Get<JsonNode>("$")!.ToJsonString());
    }
}

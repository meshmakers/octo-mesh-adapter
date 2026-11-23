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
///         service whose refusal quotes the subject token, with the error logged and with the error thrown;
///         an echoed token longer than the excerpt the node logs of an MCP answer, in a JSON body and in a
///         body the node cannot read a tool list from (the registry masks a token only as a whole, so an
///         excerpt cut inside the token would keep its head).
///     </para>
///     The node runs with a real node context, so the registry and the redaction are the pipeline's own.
/// </summary>
public class AnthropicAiQueryNodeMcpTokenMaskingTests
{
    private const string ServiceAccountConfig = "ServiceAccountConfig";
    private const string CallerToken = "test-token-caller-eyJhbGciOiJub25lIn0.caller";
    private const string DelegatedToken = "test-token-delegated-eyJhbGciOiJub25lIn0.delegated";
    private const string ServiceAccountToken = "test-token-service-account-eyJhbGciOiJub25lIn0.sa";

    /// <summary>A stretch of the long token's payload; nothing else the node logs contains it.</summary>
    private static readonly string LongTokenPayloadPart = new('p', 16);

    /// <summary>
    ///     A token as long as a real access token (about 1,000 characters): longer than both excerpts the
    ///     node logs of an MCP answer (300 and 200 characters), so each excerpt of a body that quotes it
    ///     near its start ends inside the token.
    /// </summary>
    private static readonly string LongToken =
        "test-token-long-eyJhbGciOiJub25lIn0." + new string('p', 960) + ".long";

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

        /// <summary>What every MCP request carried as its credential, as the server received it.</summary>
        public List<string?> Credentials { get; } = [];

        /// <summary>
        ///     Answers with a plain text body instead of JSON, as a proxy in front of the MCP server does;
        ///     the node reads no tool list out of it and logs the start of the body.
        /// </summary>
        public bool PlainText { get; set; }

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
            Credentials.Add(request.Headers.Authorization?.ToString());
            if (PlainText)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        $"The credential '{request.Headers.Authorization}' was rejected.", Encoding.UTF8,
                        "text/plain")
                });
            }

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
        // The server must have received the delegated token, or its echo would prove nothing.
        Assert.All(_http.Credentials, credential => Assert.Equal("Bearer " + DelegatedToken, credential));
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
        // The server must have received the service account's token, or its echo would prove nothing.
        Assert.All(_http.Credentials, credential => Assert.Equal("Bearer " + ServiceAccountToken, credential));
        var logged = EverythingLogged();
        Assert.Contains("MCP initialize failed", logged);
        Assert.DoesNotContain(ServiceAccountToken, logged);
        AssertMasked(nodeContext, ServiceAccountToken);
        Assert.DoesNotContain(ServiceAccountToken, dataContext.Get<JsonNode>("$")!.ToJsonString());
    }

    /// <summary>
    ///     No part of the long token is in what was logged: not the token, not its head (what an excerpt
    ///     cut inside the token would keep) and no stretch of its payload.
    /// </summary>
    private static void AssertNoPartOfTheLongTokenIn(string logged)
    {
        Assert.DoesNotContain(LongToken, logged);
        Assert.DoesNotContain(LongToken[..24], logged);
        Assert.DoesNotContain(LongTokenPayloadPart, logged);
    }

    [Fact]
    public async Task DelegationMode_AnEchoedTokenLongerThanTheLogExcerpt_LeavesNoPartOfItInTheLog()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(LongToken));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: true));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The server must have received the long token, or its echo would prove nothing.
        Assert.True(_http.McpRequests > 0);
        Assert.All(_http.Credentials, credential => Assert.Equal("Bearer " + LongToken, credential));
        var logged = EverythingLogged();
        // The excerpt of the 401 body was logged, with the mask where the token stood.
        Assert.Contains("MCP initialize failed: {\"error\":\"invalid_token\"", logged);
        Assert.Contains("Bearer " + PipelineSecretRegistry.Mask, logged);
        AssertNoPartOfTheLongTokenIn(logged);
        AssertMasked(nodeContext, LongToken);
    }

    [Fact]
    public async Task ServiceAccountMode_AnUnreadableToolListEchoingALongToken_LeavesNoPartOfItInTheLog()
    {
        _http.PlainText = true;
        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(_tenantRepository, ServiceAccountConfig,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(LongToken));
        var (node, dataContext, nodeContext) = Prepare(Config(delegateToCaller: false));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Handshake and tool list were both asked for with the long token.
        Assert.Equal(2, _http.McpRequests);
        Assert.All(_http.Credentials, credential => Assert.Equal("Bearer " + LongToken, credential));
        var logged = EverythingLogged();
        // Both excerpts were logged, each with the mask where the token stood.
        var maskedBody = $"The credential 'Bearer {PipelineSecretRegistry.Mask}' was rejected.";
        Assert.Contains("MCP initialize failed: " + maskedBody, logged);
        Assert.Contains("Could not parse MCP tools/list response. Raw: " + maskedBody, logged);
        AssertNoPartOfTheLongTokenIn(logged);
        AssertMasked(nodeContext, LongToken);
        Assert.DoesNotContain(LongTokenPayloadPart, dataContext.Get<JsonNode>("$")!.ToJsonString());
    }
}

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
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The credentials <c>LlmQuery@1</c> handles never reach a diagnostic output, also when the provider sits
///     at the base URL of the AiConfiguration.
///     <para>
///         Invariant: the API key and every MCP token are registered secrets of the execution before the
///         first request; the execution log, the error of the execution and the data show the mask.
///     </para>
///     <para>
///         Abuse cases, one test each: a provider at the base URL that answers 401 and echoes the
///         <c>x-api-key</c> it received, with the error logged (<c>continueOnError</c>) and with the error
///         thrown; an MCP server that echoes the delegated bearer in its 401 body.
///     </para>
///     The node runs with a real node context, so the registry and the redaction are the pipeline's own.
/// </summary>
public class LlmQueryNodeSecretMaskingTests
{
    private const string AiConfigName = "ai";
    private const string ServiceAccountConfig = "ServiceAccountConfig";
    private const string ApiKey = "test-key-foundry-sk-0123456789abcdef";
    private const string CallerToken = "test-token-caller-eyJhbGciOiJub25lIn0.caller";
    private const string DelegatedToken = "test-token-delegated-eyJhbGciOiJub25lIn0.delegated";
    private const string BaseUrl = "https://my-resource.services.ai.azure.com/anthropic";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();
    private readonly IPipelineLogger _logger = A.Fake<IPipelineLogger>();
    private readonly EchoingWire _wire = new();

    public LlmQueryNodeSecretMaskingTests()
    {
        var configuration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => configuration.IsDefined(AiConfigName)).Returns(true);
        A.CallTo(() => configuration.GetRawJson(AiConfigName))
            .Returns($$"""{"apiKey":"{{ApiKey}}","aiModel":"claude-test","baseUrl":"{{BaseUrl}}"}""");
        A.CallTo(() => _etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(configuration);
    }

    /// <summary>
    ///     The provider answers 401 quoting the key when <see cref="ProviderEchoesTheKey" /> is set, else a
    ///     plain answer; every MCP request is answered 401 quoting its bearer.
    /// </summary>
    private sealed class EchoingWire : HttpMessageHandler
    {
        public bool ProviderEchoesTheKey { get; set; }
        public List<string> ProviderKeys { get; } = [];
        public List<string?> McpCredentials { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.StartsWith(BaseUrl, StringComparison.Ordinal))
            {
                var key = string.Join(", ", request.Headers.GetValues("x-api-key"));
                ProviderKeys.Add(key);
                return Task.FromResult(ProviderEchoesTheKey
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(
                            $$$"""{"error":{"code":"401","message":"Access denied due to invalid subscription key '{{{key}}}'."}}""",
                            Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"stop_reason":"end_turn","content":[{"type":"text","text":"the answer"}]}""",
                            Encoding.UTF8, "application/json")
                    });
            }

            McpCredentials.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["error"] = "invalid_token",
                        ["error_description"] = $"The credential '{request.Headers.Authorization}' was rejected."
                    }.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }

    private (LlmQueryNode Node, DataContextImpl DataContext, INodeContext NodeContext) Prepare(
        LlmQueryNodeConfiguration config)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(_wire, false));

        var dataContext = new DataContextImpl(JsonDocument.Parse("""{"text":"some content"}"""));
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                _logger, dataContext)
            .RegisterChildNode("LlmQueryNode", 0, config, dataContext);

        return (new LlmQueryNode(A.Fake<NodeDelegate>(), _etlContext, factory, _tokenService), dataContext,
            nodeContext);
    }

    private static LlmQueryNodeConfiguration Config(bool continueOnError = false) => new()
    {
        ApiKeyConfigurationName = AiConfigName,
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        ResponseFormat = "text",
        ContinueOnError = continueOnError
    };

    private string EverythingLogged()
    {
        var text = new StringBuilder();
        foreach (var argument in Fake.GetCalls(_logger).SelectMany(call => call.Arguments))
        {
            if (argument is object[] values)
            {
                foreach (var value in values)
                {
                    text.AppendLine(value?.ToString());
                }
            }
            else
            {
                text.AppendLine(argument?.ToString());
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task AProviderAtTheBaseUrlEchoingTheKey_IsLoggedMasked()
    {
        _wire.ProviderEchoesTheKey = true;
        var (node, dataContext, nodeContext) = Prepare(Config(continueOnError: true));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The provider must have received the key, or its echo would prove nothing.
        Assert.Equal([ApiKey], _wire.ProviderKeys);
        var logged = EverythingLogged();
        Assert.Contains("HTTP 401", logged);
        Assert.Contains($"subscription key '{PipelineSecretRegistry.Mask}'", logged);
        Assert.DoesNotContain(ApiKey, logged);
        Assert.DoesNotContain(ApiKey, dataContext.Get<JsonNode>("$")!.ToJsonString());
    }

    [Fact]
    public async Task AProviderAtTheBaseUrlEchoingTheKey_IsMaskedInTheErrorOfTheExecution()
    {
        _wire.ProviderEchoesTheKey = true;
        var (node, dataContext, nodeContext) = Prepare(Config());

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal([ApiKey], _wire.ProviderKeys);
        var recorded = nodeContext.SecretRegistry!.RedactException(thrown).ToString();
        Assert.Contains("HTTP 401", recorded);
        Assert.DoesNotContain(ApiKey, recorded);
        Assert.DoesNotContain(ApiKey, EverythingLogged());
    }

    [Fact]
    public async Task CallerIdentity_AnMcpServerEchoingTheBearer_LeaksNeitherTheDelegatedNorTheCallersToken()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(DelegatedToken));
        var (node, dataContext, nodeContext) = Prepare(Config() with
        {
            McpServerUrl = "https://mcp.example.test",
            McpServiceAccountConfigName = ServiceAccountConfig,
            Identity = NodeExecutionIdentity.Caller
        });

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The server must have received the delegated token, or its echo would prove nothing.
        Assert.NotEmpty(_wire.McpCredentials);
        Assert.All(_wire.McpCredentials, credential => Assert.Equal("Bearer " + DelegatedToken, credential));
        var logged = EverythingLogged();
        Assert.Contains("MCP initialize failed", logged);
        Assert.DoesNotContain(DelegatedToken, logged);
        Assert.DoesNotContain(CallerToken, logged);

        var result = dataContext.Get<JsonNode>("$")!.ToJsonString();
        Assert.Contains("the answer", result);
        Assert.DoesNotContain(DelegatedToken, result);
        Assert.DoesNotContain(CallerToken, result);
    }
}

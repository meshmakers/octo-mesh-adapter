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
/// Covers <c>mcpDelegateToCaller</c> (AB#5031): the AI prompt path must reach the MCP server as the
/// END USER — with the user's roles and data permissions — instead of as a service account holding
/// full <c>octo_api</c> access. The mode is deliberately <b>fail-closed</b>: the two
/// degrade-to-unauthenticated paths that keep the channel assistants working must not apply here,
/// because continuing under another identity (or none) would silently defeat the very authorization
/// this mode exists to enforce.
/// </summary>
public class AnthropicAiQueryNodeDelegationTests : NodeTestBase
{
    private const string ServiceAccountConfig = "ServiceAccountConfig";
    private const string CallerToken = "ey.the.callers.token";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();
    private readonly IServiceClientAccessToken _serviceClientAccessToken = A.Fake<IServiceClientAccessToken>();

    public AnthropicAiQueryNodeDelegationTests()
    {
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.TenantId).Returns("testTenant");
    }

    private AnthropicAiQueryNode CreateNode(NodeDelegate next)
    {
        return new AnthropicAiQueryNode(next, _etlContext, A.Fake<IHttpClientFactory>(), _tokenService,
            _serviceClientAccessToken);
    }

    private (AnthropicAiQueryNode Node, INodeContext NodeContext, AnthropicAiQueryNodeConfiguration Config)
        Prepare(bool delegateToCaller, string? serviceAccountConfigName = ServiceAccountConfig)
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            Question = "Answer the user's question using the available tools.",
            McpServerUrl = "https://mcp.example.com",
            McpServiceAccountConfigName = serviceAccountConfigName,
            McpDelegateToCaller = delegateToCaller
        };

        var (_, nodeContext, next) = PrepareTest(config);
        return (CreateNode(next), nodeContext, config);
    }

    private static string? BearerOf(HttpRequestMessage request) => request.Headers.Authorization?.Parameter;

    [Fact]
    public async Task DelegationMode_SetsTheDelegatedBearerOnMcpRequests()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("delegated-token"));

        // Would be picked up by the service-account path; must NOT be used in delegation mode.
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns("service-account-token");

        var (node, nodeContext, config) = Prepare(delegateToCaller: true);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("delegated-token", BearerOf(request));

        // The service-account grant must not run at all in delegation mode.
        A.CallTo(() => _tokenService.EnsureTokenAsync(A<ITenantRepository>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DelegationMode_WithoutCallerToken_FailsClosed()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(null);

        var (node, nodeContext, config) = Prepare(delegateToCaller: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.EnsureMcpAccessTokenAsync(config, nodeContext));

        Assert.Contains("caller token", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No unauthenticated attempt: nothing was requested and no header would be produced.
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(A<ITenantRepository>._, A<string>._,
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _tokenService.EnsureTokenAsync(A<ITenantRepository>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DelegationMode_AcquisitionFails_FailsClosedInsteadOfDegrading()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(null));
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns("service-account-token");

        var (node, nodeContext, config) = Prepare(delegateToCaller: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.EnsureMcpAccessTokenAsync(config, nodeContext));

        // Explicitly NOT the "MCP calls will be sent unauthenticated" degrade of the SA path.
        Assert.Contains("Refusing", ex.Message, StringComparison.OrdinalIgnoreCase);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task DelegationMode_WithoutServiceAccountConfig_FailsClosed()
    {
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);

        var (node, nodeContext, config) = Prepare(delegateToCaller: true, serviceAccountConfigName: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.EnsureMcpAccessTokenAsync(config, nodeContext));

        Assert.Contains("mcpServiceAccountConfigName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagOff_KeepsTheServiceAccountPathUnchanged()
    {
        // The channel assistants (Teams/Signal/e-mail) must not change behaviour: the service
        // account grant runs, its token is taken from the process-wide access token, and the caller
        // token — if one exists at all — is never used.
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns("service-account-token");

        var (node, nodeContext, config) = Prepare(delegateToCaller: false);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);

        Assert.Equal("service-account-token", BearerOf(request));
        A.CallTo(() => _tokenService.EnsureTokenAsync(_tenantRepository, ServiceAccountConfig))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(A<ITenantRepository>._, A<string>._,
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FlagOff_ServiceAccountGrantThrows_StillDegradesToUnauthenticated()
    {
        // AB#4541: a broken ServiceAccountConfiguration must keep the (tool-less) chat working in
        // the non-delegating mode. Pinned here so the fail-closed delegation branch above cannot be
        // widened into this path by accident.
        A.CallTo(() => _tokenService.EnsureTokenAsync(_tenantRepository, ServiceAccountConfig))
            .Throws(new InvalidOperationException("Malformed URL"));
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns(null);

        var (node, nodeContext, config) = Prepare(delegateToCaller: false);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);
        Assert.Null(request.Headers.Authorization);
    }

    // ------------------------------------------------------ AB#5127: identity alias, both directions

    private (AnthropicAiQueryNode Node, INodeContext NodeContext, AnthropicAiQueryNodeConfiguration Config)
        PrepareWithIdentity(NodeExecutionIdentity identity, bool mcpDelegateToCaller = false)
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            Question = "Answer the user's question using the available tools.",
            McpServerUrl = "https://mcp.example.com",
            McpServiceAccountConfigName = ServiceAccountConfig,
            McpDelegateToCaller = mcpDelegateToCaller,
            Identity = identity
        };

        var (_, nodeContext, next) = PrepareTest(config);
        return (CreateNode(next), nodeContext, config);
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller)]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    [InlineData(NodeExecutionIdentity.System)]
    public void DelegatesToCaller_ResolvesTheAlias(NodeExecutionIdentity identity)
    {
        // identity: Caller (or legacy mcpDelegateToCaller: true) is the only combination that delegates.
        Assert.Equal(identity == NodeExecutionIdentity.Caller,
            new AnthropicAiQueryNodeConfiguration { Question = "q", Identity = identity }.DelegatesToCaller);

        Assert.True(new AnthropicAiQueryNodeConfiguration
        {
            Question = "q", Identity = identity, McpDelegateToCaller = true
        }.DelegatesToCaller);
    }

    [Fact]
    public async Task IdentityCaller_DelegatesLikeMcpDelegateToCallerTrue()
    {
        // identity: Caller is the general spelling of mcpDelegateToCaller: true — same delegated token.
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("delegated-token"));
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns("service-account-token");

        var (node, nodeContext, config) =
            PrepareWithIdentity(NodeExecutionIdentity.Caller, mcpDelegateToCaller: false);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);

        Assert.Equal("delegated-token", BearerOf(request));
        A.CallTo(() => _tokenService.EnsureTokenAsync(A<ITenantRepository>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task IdentityServiceAccount_UsesTheServiceAccountLikeFlagOff()
    {
        // identity: ServiceAccount (the AI node's default) is the general spelling of the flag being
        // absent — the service account's own token, never the caller's.
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _serviceClientAccessToken.AccessToken).Returns("service-account-token");

        var (node, nodeContext, config) = PrepareWithIdentity(NodeExecutionIdentity.ServiceAccount);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);

        Assert.Equal("service-account-token", BearerOf(request));
        A.CallTo(() => _tokenService.EnsureTokenAsync(_tenantRepository, ServiceAccountConfig))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(A<ITenantRepository>._, A<string>._,
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task LegacyMcpDelegateToCaller_StillDelegates_EvenWhenIdentityIsServiceAccount()
    {
        // Backward compatibility: a pipeline that only knows the old flag keeps delegating even though
        // the new property defaults to ServiceAccount — the "caller" spelling wins.
        A.CallTo(() => _etlContext.CallerAccessToken).Returns(CallerToken);
        A.CallTo(() => _tokenService.AcquireDelegatedTokenAsync(_tenantRepository, ServiceAccountConfig,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("delegated-token"));

        var (node, nodeContext, config) =
            PrepareWithIdentity(NodeExecutionIdentity.ServiceAccount, mcpDelegateToCaller: true);

        await node.EnsureMcpAccessTokenAsync(config, nodeContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/testTenant/mcp");
        node.AddMcpAuthHeader(request);

        Assert.Equal("delegated-token", BearerOf(request));
    }
}

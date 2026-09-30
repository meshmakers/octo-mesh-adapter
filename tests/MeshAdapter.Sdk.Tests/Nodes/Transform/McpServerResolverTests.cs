using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the contracts of <see cref="McpServerResolver"/>: unknown transport values fall back
/// to the documented Sse default instead of an undefined enum member, the <c>{tenantId}</c>
/// placeholder promised by the CK attribute is substituted, a configured service account is
/// required (a failing token acquisition fails closed rather than calling the server under the
/// static bearer or anonymously), and credentialed endpoints must be HTTPS.
/// </summary>
public class McpServerResolverTests
{
    private const string TenantId = "tenant-1";

    private static IMeshEtlContext EtlContextWith(string name, string rawJson)
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined(name)).Returns(true);
        A.CallTo(() => globalConfiguration.GetRawJson(name)).Returns(rawJson);

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => etlContext.TenantId).Returns(TenantId);
        A.CallTo(() => etlContext.TenantRepository).Returns(A.Fake<ITenantRepository>());
        return etlContext;
    }

    [Theory]
    [InlineData("0", "Stdio")]
    [InlineData("2", "Http")]
    [InlineData("\"http\"", "Http")]
    [InlineData("\"Sse\"", "Sse")]
    public void ParseTransport_KnownValues_ParsedWithoutWarning(string json, string expected)
    {
        var nodeContext = A.Fake<INodeContext>();

        var transport = McpServerResolver.ParseTransport(JToken.Parse(json), "srv", nodeContext);

        Assert.Equal(expected, transport.ToString());
        A.CallTo(() => nodeContext.Warning(A<string>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("\"grpc\"")]
    public void ParseTransport_UnknownValue_FallsBackToSseWithWarning(string json)
    {
        var nodeContext = A.Fake<INodeContext>();

        var transport = McpServerResolver.ParseTransport(JToken.Parse(json), "srv", nodeContext);

        Assert.Equal(McpServerResolver.McpTransport.Sse, transport);
        Assert.True(Enum.IsDefined(typeof(McpServerResolver.McpTransport), transport));
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains("unknown transport"))).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Resolve_UrlWithTenantPlaceholder_SubstitutesTheExecutingTenant()
    {
        var etlContext = EtlContextWith("srv",
            """{"url":"https://mcp.example.com/{tenantId}/mcp","transport":"Http"}""");

        var servers = McpServerResolver.Resolve(["srv"], etlContext, A.Fake<INodeContext>());

        var server = Assert.Single(servers);
        Assert.Equal($"https://mcp.example.com/{TenantId}/mcp", server.Url);
    }

    [Fact]
    public async Task ApplyServiceAccountTokensAsync_AcquisitionThrows_FailsClosedInsteadOfUsingTheStaticToken()
    {
        var etlContext = EtlContextWith("srv", "{}");
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, "broken-sa",
                A<CancellationToken>._))
            .Throws(new InvalidOperationException("Malformed URL"));
        IList<McpServerResolver.McpServerConfig> servers = [Server("first", "static-token", "broken-sa")];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpServerResolver.ApplyServiceAccountTokensAsync(
                servers, tokenService, etlContext, A.Fake<INodeContext>(), CancellationToken.None));

        // The configured identity is required: no silent fallback to the static bearer.
        Assert.Contains("'first'", ex.Message);
        Assert.Contains("broken-sa", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("static-token", servers[0].BearerToken);
    }

    [Fact]
    public async Task ApplyServiceAccountTokensAsync_AcquisitionYieldsNoToken_FailsClosed()
    {
        var etlContext = EtlContextWith("srv", "{}");
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, "unusable-sa",
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(null));
        IList<McpServerResolver.McpServerConfig> servers = [Server("first", null, "unusable-sa")];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpServerResolver.ApplyServiceAccountTokensAsync(
                servers, tokenService, etlContext, A.Fake<INodeContext>(), CancellationToken.None));

        Assert.Contains("unusable-sa", ex.Message);
    }

    [Fact]
    public async Task ApplyServiceAccountTokensAsync_HealthyConfiguration_ReplacesTheStaticToken()
    {
        var etlContext = EtlContextWith("srv", "{}");
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, "good-sa",
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("fresh-token"));
        IList<McpServerResolver.McpServerConfig> servers = [Server("second", "static-token", "good-sa")];

        var result = await McpServerResolver.ApplyServiceAccountTokensAsync(
            servers, tokenService, etlContext, A.Fake<INodeContext>(), CancellationToken.None);

        Assert.Equal("fresh-token", result[0].BearerToken);
    }

    [Fact]
    public async Task ApplyServiceAccountTokensAsync_UpstreamCancellation_IsNotSwallowed()
    {
        var etlContext = EtlContextWith("srv", "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, A<string>._,
                A<CancellationToken>._))
            .Throws(new OperationCanceledException(cts.Token));
        IList<McpServerResolver.McpServerConfig> servers = [Server("first", null, "sa")];

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            McpServerResolver.ApplyServiceAccountTokensAsync(
                servers, tokenService, etlContext, A.Fake<INodeContext>(), cts.Token));
    }

    [Theory]
    [InlineData("http://mcp.example.com/mcp", "token", true)]
    [InlineData("http://mcp.example.com/mcp", null, false)]
    [InlineData("https://mcp.example.com/mcp", "token", false)]
    [InlineData("http://localhost:5017/mcp", "token", false)]
    [InlineData("http://127.0.0.1:5017/mcp", "token", false)]
    public void RequireHttpsForCredentials_RejectsOnlyClearTextRemoteEndpointsThatCarryCredentials(
        string url, string? bearer, bool expectReject)
    {
        var server = new McpServerResolver.McpServerConfig("srv", url, McpServerResolver.McpTransport.Http,
            null, null, bearer, new Dictionary<string, string>(), null);

        var act = () => McpServerResolver.RequireHttpsForCredentials(server, new Uri(url));

        if (expectReject)
        {
            var ex = Assert.Throws<InvalidOperationException>(act);
            Assert.Contains("not TLS", ex.Message);
        }
        else
        {
            act();
        }
    }

    [Fact]
    public void RequireHttpsForCredentials_AdditionalHeadersCountAsCredentials()
    {
        var server = new McpServerResolver.McpServerConfig("srv", "http://mcp.example.com/mcp",
            McpServerResolver.McpTransport.Http, null, null, null,
            new Dictionary<string, string> { ["x-api-key"] = "k" }, null);

        Assert.Throws<InvalidOperationException>(
            () => McpServerResolver.RequireHttpsForCredentials(server, new Uri(server.Url!)));
    }

    [Fact]
    public void BuildTransport_ClearTextEndpointWithBearer_IsRejectedBeforeAnyConnection()
    {
        var server = Server("srv", "token", "sa") with { Url = "http://mcp.example.com/mcp" };

        Assert.Throws<InvalidOperationException>(() => McpServerResolver.BuildTransport(server));
    }

    private static McpServerResolver.McpServerConfig Server(string name, string? bearer, string serviceAccount) =>
        new(name, "https://mcp.example.com/mcp", McpServerResolver.McpTransport.Http, null, null, bearer,
            new Dictionary<string, string>(), serviceAccount);
}

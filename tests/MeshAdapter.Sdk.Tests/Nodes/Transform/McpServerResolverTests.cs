using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the tolerant parts of <see cref="McpServerResolver"/>: unknown transport values fall back
/// to the documented Sse default instead of an undefined enum member, the <c>{tenantId}</c>
/// placeholder promised by the CK attribute is substituted, and a failing service-account token
/// acquisition degrades that one server (static bearer) instead of aborting the caller.
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
    public async Task ApplyServiceAccountTokensAsync_AcquisitionThrows_DegradesThatServerToTheStaticToken()
    {
        var etlContext = EtlContextWith("srv", "{}");
        var nodeContext = A.Fake<INodeContext>();
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, "broken-sa",
                A<CancellationToken>._))
            .Throws(new InvalidOperationException("Malformed URL"));
        A.CallTo(() => tokenService.GetAccessTokenAsync(A<ITenantRepository>._, A<string>._, "good-sa",
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("fresh-token"));
        IList<McpServerResolver.McpServerConfig> servers =
        [
            Server("first", "static-token", "broken-sa"),
            Server("second", null, "good-sa")
        ];

        var result = await McpServerResolver.ApplyServiceAccountTokensAsync(
            servers, tokenService, etlContext, nodeContext, CancellationToken.None);

        // The broken configuration degrades ONE server and keeps its static bearer ...
        Assert.Equal("static-token", result[0].BearerToken);
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains("'first'"))).MustHaveHappenedOnceExactly();
        // ... while the healthy one still receives its service-account token.
        Assert.Equal("fresh-token", result[1].BearerToken);
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

    private static McpServerResolver.McpServerConfig Server(string name, string? bearer, string serviceAccount) =>
        new(name, "https://mcp.example.com/mcp", McpServerResolver.McpTransport.Http, null, null, bearer,
            new Dictionary<string, string>(), serviceAccount);
}

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
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     <c>McpToolCall@1</c> (AB#8658) against a scripted MCP endpoint. The real-server conformance test is
///     <c>McpToolCallNodeIntegrationTests</c> (in-process ModelContextProtocol server).
/// </summary>
public class McpToolCallNodeTests : NodeTestBase
{
    private const string Token = "eyJ.curator-token.sig";
    private const string AccountName = "knowledge-curator";

    /// <summary>Scripted MCP endpoint: answers by JSON-RPC method and records every request.</summary>
    private sealed class FakeMcpServer : HttpMessageHandler
    {
        public List<(string Method, string? Authorization, string? SessionId, JsonNode? Body)> Requests { get; } = [];
        public Func<JsonNode, HttpResponseMessage>? OnToolsCall { get; set; }
        public HttpStatusCode InitializeStatus { get; set; } = HttpStatusCode.OK;
        public Action<HttpRequestMessage>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            OnRequest?.Invoke(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = body?["method"]?.GetValue<string>() ?? "?";
            Requests.Add((method, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.First() : null, body));

            switch (method)
            {
                case "initialize":
                    var init = InitializeStatus == HttpStatusCode.OK
                        ? Json(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"}}}""")
                        : new HttpResponseMessage(InitializeStatus);
                    init.Headers.Add("Mcp-Session-Id", "session-1");
                    return init;
                case "notifications/initialized":
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                case "tools/call":
                    return OnToolsCall?.Invoke(body!) ?? Json(HttpStatusCode.OK, ToolResult("""{"ok":true}"""));
                default:
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static string ToolResult(string text, bool isError = false) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError
            }
        }.ToJsonString();

    private sealed record Harness(
        McpToolCallNode Node,
        IDataContext DataContext,
        INodeContext NodeContext,
        NodeDelegate Next,
        FakeMcpServer Server,
        IServiceAccountTokenService TokenService,
        IGlobalConfiguration GlobalConfiguration);

    private Harness Prepare(McpToolCallNodeConfiguration config, string? token = Token,
        JsonObject? arguments = null)
    {
        var server = new FakeMcpServer();
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(server, false));

        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, A<string>._,
            A<CancellationToken>._)).Returns(token);

        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined("AiConfig")).Returns(true);
        A.CallTo(() => globalConfiguration.GetRawJson("AiConfig"))
            .Returns("""{"apiKey":"x","mcpServerUrl":"https://mcp.example.test/"}""");

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns("tenant1");
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);

        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.args")).Returns(arguments == null ? DataKind.Undefined : DataKind.Object);
        A.CallTo(() => dataContext.Get<JsonNode>("$.args")).Returns(arguments);
        A.CallTo(() => dataContext.GetKind("$.toolChoice")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.toolChoice")).Returns("knowledge_delete_everything");

        var node = new McpToolCallNode(next, etlContext, factory, tokenService);
        return new Harness(node, dataContext, nodeContext, next, server, tokenService, globalConfiguration);
    }

    private static McpToolCallNodeConfiguration Config() => new()
    {
        AllowedTools = ["knowledge_upsert_page", "knowledge_link"],
        ToolName = "knowledge_upsert_page",
        ArgumentsPath = "$.args",
        AiConfigurationName = "AiConfig",
        McpServiceAccountConfigName = AccountName,
        TargetPath = "$.result"
    };

    private static JsonObject PageArgs() => new() { ["title"] = "Netzlaufwerk", ["markdown"] = "# A" };

    // ---- AC1: allowlisted call, service identity, structured result ----

    [Fact]
    public async Task AllowedTool_IsCalledWithTheArgumentsAsTheServiceAccount_AndTheResultIsWritten()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK, ToolResult("""{"rtId":"670000000000000000000001","version":3}"""));

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        Assert.Equal(["initialize", "notifications/initialized", "tools/call"], h.Server.Requests.Select(r => r.Method));
        Assert.All(h.Server.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        // The session id handed out on initialize travels with every later request.
        Assert.All(h.Server.Requests.Skip(1), r => Assert.Equal("session-1", r.SessionId));

        var call = h.Server.Requests[2].Body!["params"]!;
        Assert.Equal("knowledge_upsert_page", call["name"]!.GetValue<string>());
        Assert.Equal("Netzlaufwerk", call["arguments"]!["title"]!.GetValue<string>());

        A.CallTo(() => h.TokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, AccountName,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        var written = Assert.Single(new DataContextWrites(h.DataContext).All, w => w.Path == "$.result").Value;
        Assert.True(Matches(written), "unexpected result written: " + written);

        static bool Matches(object? o) => o is JsonObject && ((JsonObject)o!)["version"]!.GetValue<int>() == 3;
        VerifyNextCalled(h.Next, h.DataContext, h.NodeContext);
    }

    [Fact]
    public async Task Endpoint_IsTheAiConfigurationUrl_WithTheTenant()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        Uri? requested = null;
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK, ToolResult("{}"));
        h.Server.OnRequest = r => requested ??= r.RequestUri;

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        Assert.Equal("https://mcp.example.test/tenant1/mcp", requested!.ToString());
    }

    [Fact]
    public async Task NodeUrl_IsTheFallbackWithoutAnAiConfiguration()
    {
        var h = Prepare(Config() with { AiConfigurationName = null, McpServerUrl = "https://localhost:5017" },
            arguments: PageArgs());
        Uri? requested = null;
        h.Server.OnRequest = r => requested ??= r.RequestUri;

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        Assert.Equal("https://localhost:5017/tenant1/mcp", requested!.ToString());
    }

    [Fact]
    public async Task StructuredContent_WinsOverTheText()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ignored"}],"structuredContent":{"rtId":"x"}}}""");

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        var written = Assert.Single(new DataContextWrites(h.DataContext).All, w => w.Path == "$.result").Value;
        Assert.True(Matches(written), "unexpected result written: " + written);

        static bool Matches(object? o) => o is JsonObject && ((JsonObject)o!)["rtId"]!.GetValue<string>() == "x";
    }

    [Fact]
    public async Task SseAnswer_WithANotificationFirst_IsRead()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Sse(
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n" +
            $"event: message\ndata: {ToolResult("""{"done":true}""")}\n\n");

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        var written = Assert.Single(new DataContextWrites(h.DataContext).All, w => w.Path == "$.result").Value;
        Assert.True(Matches(written), "unexpected result written: " + written);

        static bool Matches(object? o) => o is JsonObject && ((JsonObject)o!)["done"]!.GetValue<bool>();
    }

    [Fact]
    public async Task PlainTextResult_IsWrittenAsText()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK, ToolResult("Page saved."));

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        var written = Assert.Single(new DataContextWrites(h.DataContext).All, w => w.Path == "$.result").Value;
        Assert.True(Matches(written), "unexpected result written: " + written);

        static bool Matches(object? o) => o is JsonValue && ((JsonValue)o!).GetValue<string>() == "Page saved.";
    }

    // ---- AC2: allowlist before any call ----

    [Fact]
    public async Task ToolNotInTheAllowlist_FailsBeforeAnyTokenOrNetworkCall()
    {
        var config = Config() with { ToolName = "delete_entities" };
        var h = Prepare(config, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("'delete_entities' is not in this node's allowedTools", ex.Message);
        Assert.Empty(h.Server.Requests);
        A.CallTo(() => h.TokenService.AcquireServiceAccountTokenAsync(A<ITenantRepository>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        VerifyNextNotCalled(h.Next, h.DataContext, h.NodeContext);
    }

    [Fact]
    public async Task ToolNameFromTheDataContext_IsCheckedAgainstTheAllowlist_AndContinueOnErrorDoesNotApply()
    {
        var config = Config() with { ToolName = null, ToolNamePath = "$.toolChoice", ContinueOnError = true };
        var h = Prepare(config, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("'knowledge_delete_everything' is not in this node's allowedTools", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task AllowlistMatch_IsCaseSensitive()
    {
        var h = Prepare(Config() with { ToolName = "Knowledge_Upsert_Page" }, arguments: PageArgs());

        await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Empty(h.Server.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyAllowlist_IsAConfigurationError(string entry)
    {
        var h = Prepare(Config() with { AllowedTools = entry.Length == 0 ? [] : [entry] }, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("'allowedTools' is required", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task ToolNameAndToolNamePathTogether_IsAConfigurationError()
    {
        var h = Prepare(Config() with { ToolNamePath = "$.toolChoice" }, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("exactly one of 'toolName' and 'toolNamePath'", ex.Message);
    }

    [Fact]
    public async Task ArgumentsThatAreNotAnObject_FailBeforeAnyCall()
    {
        var h = Prepare(Config());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("'argumentsPath' $.args does not resolve to a JSON object", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    // ---- Identity: fail-closed ----

    [Fact]
    public async Task NoServiceAccount_IsAConfigurationError()
    {
        var h = Prepare(Config() with { McpServiceAccountConfigName = null }, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("'mcpServiceAccountConfigName' is required", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task NoToken_NeverSendsAnUnauthenticatedRequest()
    {
        var h = Prepare(Config(), token: null, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("No access token could be acquired via ServiceAccountConfiguration 'knowledge-curator'", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task NoUsableUrl_IsAConfigurationError()
    {
        var h = Prepare(Config() with { AiConfigurationName = "Missing" }, arguments: PageArgs());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("No usable MCP server URL", ex.Message);
        Assert.Empty(h.Server.Requests);
    }

    // ---- AC3: MCP errors surface with code and message ----

    [Fact]
    public async Task JsonRpcError_FailsTheNodeWithTheMcpCodeAndMessage()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"Invalid params: title is required"}}""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("MCP error -32602", ex.Message);
        Assert.Contains("Invalid params: title is required", ex.Message);
        VerifyNextNotCalled(h.Next, h.DataContext, h.NodeContext);
    }

    [Fact]
    public async Task ToolErrorResult_FailsTheNodeWithTheToolsMessage()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK,
            ToolResult("""{"error":{"code":"CONTENT_GUARD","message":"Content looks like a credential"}}""", isError: true));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("MCP tool 'knowledge_upsert_page' returned an error", ex.Message);
        Assert.Contains("CONTENT_GUARD", ex.Message);
        Assert.Contains("Content looks like a credential", ex.Message);
    }

    [Fact]
    public async Task HttpErrorWithoutJsonRpc_NamesTheStatus()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.InitializeStatus = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("MCP server answered initialize with HTTP 401", ex.Message);
        Assert.Single(h.Server.Requests);
    }

    [Fact]
    public async Task TheTokenNeverAppearsInTheError_EvenWhenTheServerEchoesIt()
    {
        var h = Prepare(Config(), arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.Forbidden, $"forbidden for Bearer {Token}");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext));

        Assert.Contains("HTTP 403", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain(Token, ex.ToString());
    }

    [Fact]
    public async Task ContinueOnError_LogsTheFailureAndContinues()
    {
        var h = Prepare(Config() with { ContinueOnError = true }, arguments: PageArgs());
        h.Server.OnToolsCall = _ => Json(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32603,"message":"boom"}}""");

        await h.Node.ProcessObjectAsync(h.DataContext, h.NodeContext);

        VerifyNextCalled(h.Next, h.DataContext, h.NodeContext);
        Assert.Empty(new DataContextWrites(h.DataContext).All);
    }

    [Fact]
    public void Redact_ReplacesEveryOccurrenceOfTheToken()
    {
        Assert.Equal("a *** b ***", McpToolCallNode.Redact($"a {Token} b {Token}", Token));
        Assert.Equal("unchanged", McpToolCallNode.Redact("unchanged", null));
    }
}

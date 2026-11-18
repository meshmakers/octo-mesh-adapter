using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     Golden requests of <c>AnthropicAiQuery@1</c>: every HTTP request the node sends for three real
///     pipeline shapes (document analysis, chat with MCP tools on the caller's identity, knowledge
///     generation from a prompt template) is compared <b>byte for byte</b> with a recorded file, together
///     with the data the node leaves in the data context.
///     <para>
///         The files were recorded from the node before its internals were moved behind the shared LLM
///         engine. They are the proof that a deployed pipeline sends the same request afterwards: cache
///         breakpoints, <c>output_config</c>, headers, the MCP handshake and the order of every property.
///         The other test classes of this node compare parsed JSON, which a changed property order, a
///         changed number format or a lost <c>cache_control</c> on an unasserted block would pass.
///     </para>
///     <para>
///         A deliberate change of the wire format is recorded again with the environment variable
///         <c>OCTO_UPDATE_GOLDEN_REQUESTS=1</c> and reviewed as a diff of the files in
///         <c>Resources/GoldenRequests/AnthropicAiQuery</c>.
///     </para>
/// </summary>
public class AnthropicAiQueryNodeGoldenRequestTests
{
    private const string AiConfigName = "AnthropicAiConfig";
    private const string ServiceAccountConfigName = "McpServiceAccount";
    private const string TemplateName = "knowledge-ingest";
    private const string ApiKey = "test-key-golden-0001";
    private const string DelegatedToken = "test-token-delegated-0002";
    private const string CallerToken = "test-token-caller-0003";
    private const string McpServerUrl = "https://mcp.example.test";

    // ---- scenario 1: document analysis (JSON answer without a schema) ----

    [Fact]
    public async Task DocumentAnalysis_JsonWithoutSchema()
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKeyConfigurationName = AiConfigName,
            Question = "Extract the invoice header. Use null for values the document does not state.",
            Path = "$.document.text",
            DataPaths = ["$.document.fileName", "$.document.meta", "$.document.pageCount", "$.missing"],
            MaxTokens = 2000,
            Temperature = 0.1,
            ResponseFormat = "json",
            ContinueOnError = true,
            JsonFormatSample = """
                               {
                                 "invoiceNumber": "RE-2026-0815",
                                 "grossTotal": 1190.00
                               }
                               """,
            TargetPath = "$.ai.result"
        };

        var scenario = new Scenario(
            """
            {
              "document": {
                "fileName": "Rechnung Müller & Söhne.pdf",
                "pageCount": 2,
                "meta": { "source": "e-mail", "tags": ["eingang", "2026"], "amount": 1190.00 },
                "text": "RECHNUNG Nr. RE-2026-0815\nMüller & Söhne GmbH — „Qualität seit 1950“\n<Betrag> 1.190,00 € 😀"
              }
            }
            """);
        scenario.Configuration[AiConfigName] =
            ("""{"apiKey":"test-key-golden-0001","aiModel":"claude-sonnet-4-6"}""", null);
        scenario.AnthropicAnswers.Enqueue(AnthropicText(
            """{"invoiceNumber":"RE-2026-0815","grossTotal":1190.00}""", 812, 41));

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("document-analysis");
    }

    // ---- scenario 2: chat with MCP tools on the caller's identity ----

    [Fact]
    public async Task ChatWithMcpTools_DelegatedToTheCaller()
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKeyConfigurationName = AiConfigName,
            Question = "Answer the user's message. Use the tools for every figure you state.",
            SystemPrompt = "You are the accounting assistant of the tenant. Answer in the user's language.",
            DataPaths = ["$.input.message"],
            ConversationHistoryPath = "$.input.history",
            ResponseFormat = "text",
            MaxTokens = 500,
            Temperature = 0.3,
            MaxToolRounds = 6,
            McpServiceAccountConfigName = ServiceAccountConfigName,
            McpDelegateToCaller = true,
            McpToolNames = ["query_entities", "get_entity"],
            TargetPath = "$.answer"
        };

        var scenario = new Scenario(
            """
            {
              "input": {
                "message": "Welche Rechnungen sind offen?",
                "history": [
                  { "role": "user", "content": "Hallo" },
                  { "role": "assistant", "content": "Guten Tag, wie kann ich helfen?" },
                  { "role": "", "content": "dropped: no role" },
                  { "role": "user", "content": "" }
                ]
              }
            }
            """);
        scenario.Configuration[AiConfigName] =
        (
            """{"apiKey":"test-key-golden-0001","aiModel":"claude-opus-4-8","maxTokens":4000,"temperature":0.2,"mcpServerUrl":"https://mcp.example.test"}""",
            null);
        scenario.Principal = new VerifiedPrincipal("subject-1", "goldenTenant", "erika@example.test",
            "Erika Mustermann", ["Accountant", "Reader"]);
        scenario.CallerAccessToken = CallerToken;

        // tools/list answers as an SSE stream; one tool has no input schema, one is not on the allowlist.
        scenario.McpAnswers["tools/list"] = _ => Sse(
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"query_entities","description":"Queries runtime entities — „Rechnungen“ <all>","inputSchema":{"type":"object","properties":{"ckTypeId":{"type":"string"},"take":{"type":"integer","default":50}},"required":["ckTypeId"]}},{"name":"delete_entity","description":"Deletes an entity","inputSchema":{"type":"object"}},{"name":"get_entity"}]}}""");
        scenario.McpAnswers["tools/call"] = body =>
            body["params"]!["name"]!.GetValue<string>() == "query_entities"
                ? Json(
                    """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"[{\"number\":\"RE-1\",\"open\":120.5}]"},{"type":"text","text":"1 row"}]}}""")
                : Json("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Entity not found"}}""");

        scenario.AnthropicAnswers.Enqueue(
            """{"id":"msg_1","type":"message","role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"Ich sehe nach."},{"type":"tool_use","id":"toolu_01","name":"query_entities","input":{"ckTypeId":"Basic.Accounting/Invoice","take":10}},{"type":"tool_use","id":"toolu_02","name":"get_entity","input":{"rtId":"650000000000000000000009"}}],"usage":{"input_tokens":1500,"output_tokens":90,"cache_creation_input_tokens":1200}}""");
        scenario.AnthropicAnswers.Enqueue(AnthropicText("Offen ist RE-1 mit 120,50 €.", 300, 25, cacheRead: 1200));

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("chat-mcp-delegation");
    }

    // ---- scenario 3: knowledge generation from a prompt template (schema, cached source, repair call) ----

    [Fact]
    public async Task KnowledgeGeneration_FromATemplate_WithSchemaCachedSourceAndARepairCall()
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKeyConfigurationName = AiConfigName,
            PromptTemplateName = TemplateName,
            Path = "$.source.text",
            DataPaths = ["$.draft"],
            ResponseFormat = "json",
            JsonSchema =
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "minLength": 3 },
                    "facts": { "type": "array", "items": { "type": "string" }, "maxItems": 5 }
                  },
                  "required": ["title", "facts"]
                }
                """,
            MaxJsonRepairAttempts = 1,
            UsageOutputPath = "$.usage",
            CacheSource = true,
            TargetPath = "$.knowledge"
        };

        var scenario = new Scenario(
            """
            {
              "source": { "text": "OctoMesh pipelines are YAML-defined ETL data flows executed by adapters." },
              "draft": { "title": "Pipelines", "facts": [] }
            }
            """);
        scenario.Configuration[AiConfigName] =
        (
            """{"apiKey":"test-key-golden-0001","aiModel":"claude-config-model","maxTokens":2000,"temperature":0.7}""",
            null);
        scenario.Configuration[TemplateName] =
        (
            """{"rtId":"650000000000000000000001","rtWellKnownName":"knowledge-ingest","attributes":{"Name":"knowledge-ingest","SystemPromptFragment":"You turn source documents into knowledge entries.","TemplateVersion":3,"PromptPurpose":1,"UserTemplate":"Write the knowledge entry for the source.","DefaultModelId":"claude-sonnet-4-6","Temperature":0.3,"MaxTokens":4000,"FewShotExamples":"<example>{\"title\":\"Adapters\",\"facts\":[\"An adapter executes pipelines.\"]}</example>"}}""",
            AiPromptTemplateResolver.AiPromptTemplateTypeFullName);

        // The first answer misses the required "facts": one bounded repair call follows.
        scenario.AnthropicAnswers.Enqueue(AnthropicText("""{"title":"Pipelines"}""", 2100, 12, cacheCreation: 1800));
        scenario.AnthropicAnswers.Enqueue(AnthropicText(
            """{"title":"Pipelines","facts":["Pipelines are YAML-defined."]}""", 260, 22));

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("knowledge-generation");
    }

    // ---- helpers ----

    private static string AnthropicText(string text, int input, int output, int cacheRead = 0,
        int cacheCreation = 0) =>
        new JsonObject
        {
            ["id"] = "msg_golden",
            ["type"] = "message",
            ["role"] = "assistant",
            ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["output_tokens"] = output,
                ["cache_read_input_tokens"] = cacheRead,
                ["cache_creation_input_tokens"] = cacheCreation
            }
        }.ToJsonString();

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string message) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {message}\n\n", Encoding.UTF8, "text/event-stream")
        };

    /// <summary>
    ///     One recorded run: a real data context and node context, a scripted Anthropic API and MCP server
    ///     behind the node's <see cref="IHttpClientFactory" />, and the transcript of everything sent.
    /// </summary>
    private sealed class Scenario(string dataJson)
    {
        private readonly StringBuilder _transcript = new();
        private int _requestNumber;
        private string? _finalData;

        public Dictionary<string, (string Json, string? Type)> Configuration { get; } = new();
        public Queue<string> AnthropicAnswers { get; } = new();
        public Dictionary<string, Func<JsonNode, HttpResponseMessage>> McpAnswers { get; } = new();
        public VerifiedPrincipal? Principal { get; set; }
        public string? CallerAccessToken { get; set; }

        public async Task RunAsync(AnthropicAiQueryNodeConfiguration config)
        {
            var globalConfiguration = A.Fake<IGlobalConfiguration>();
            A.CallTo(() => globalConfiguration.IsDefined(A<string>._))
                .ReturnsLazily((string name) => Configuration.ContainsKey(name));
            A.CallTo(() => globalConfiguration.GetRawJson(A<string>._))
                .ReturnsLazily((string name) => Configuration[name].Json);
            A.CallTo(() => globalConfiguration.GetConfigurationTypeId(A<string>._))
                .ReturnsLazily((string name) => Configuration.TryGetValue(name, out var entry) && entry.Type != null
                    ? new RtCkId<CkTypeId>(entry.Type)
                    : null);

            var tenantRepository = A.Fake<ITenantRepository>();
            var etlContext = A.Fake<IMeshEtlContext>();
            A.CallTo(() => etlContext.TenantId).Returns("goldenTenant");
            A.CallTo(() => etlContext.TenantRepository).Returns(tenantRepository);
            A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
            A.CallTo(() => etlContext.VerifiedPrincipal).Returns(Principal);
            A.CallTo(() => etlContext.CallerAccessToken).Returns(CallerAccessToken);

            var tokenService = A.Fake<IServiceAccountTokenService>();
            A.CallTo(() => tokenService.AcquireDelegatedTokenAsync(tenantRepository, ServiceAccountConfigName,
                    CallerToken, A<CancellationToken>._))
                .Returns(Task.FromResult<string?>(DelegatedToken));

            var handler = new Wire(this);
            var factory = A.Fake<IHttpClientFactory>();
            A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler, false));

            using var document = JsonDocument.Parse(dataJson);
            using var dataContext = new DataContextImpl(document.RootElement);
            var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                    A.Fake<IPipelineLogger>(), dataContext)
                .RegisterChildNode("AnthropicAiQueryNode", 0, config, dataContext);

            var node = new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory, tokenService);
            await node.ProcessObjectAsync(dataContext, nodeContext);

            _finalData = dataContext.Get<JsonNode>("$")?.ToJsonString();
        }

        public void AssertMatchesGolden(string name, [CallerFilePath] string testFilePath = "")
        {
            var actual = _transcript + "=== data context after the node ===\n" + _finalData + "\n";
            var relativePath = Path.Combine("Resources", "GoldenRequests", "AnthropicAiQuery", name + ".txt");

            if (Environment.GetEnvironmentVariable("OCTO_UPDATE_GOLDEN_REQUESTS") == "1")
            {
                // Nodes/Transform/<this file> → the test project folder.
                var projectDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
                var target = Path.Combine(projectDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, actual, new UTF8Encoding(false));
                return;
            }

            var goldenPath = Path.Combine(AppContext.BaseDirectory, relativePath);
            Assert.True(File.Exists(goldenPath),
                $"Golden file '{relativePath}' is missing. Record it with OCTO_UPDATE_GOLDEN_REQUESTS=1.");

            // A checkout may convert the line ends of the file; the request bodies are single lines.
            var expected = File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n");
            Assert.Equal(expected, actual);
        }

        /// <summary>
        ///     A credential is recorded by what it is, never by its value: the golden file states that the
        ///     API key went to the Anthropic API and the delegated token to the MCP server, and any other
        ///     value at that place fails the comparison.
        /// </summary>
        private static string Describe(string headerName, string value)
        {
            if (!headerName.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) &&
                !headerName.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return value switch
            {
                ApiKey => "<the api key of the AiConfiguration>",
                "Bearer " + DelegatedToken => "Bearer <the delegated token of the caller>",
                "Bearer " + CallerToken => "Bearer <THE CALLER'S OWN TOKEN - must never be sent>",
                _ => "<AN UNEXPECTED CREDENTIAL>"
            };
        }

        private sealed class Wire(Scenario scenario) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                var headers = request.Headers
                    .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .Select(h => (Name: h.Key.ToLowerInvariant(), Value: string.Join(", ", h.Value)))
                    .OrderBy(h => h.Name, StringComparer.Ordinal);

                var transcript = scenario._transcript;
                transcript.Append("=== request ").Append(++scenario._requestNumber).Append(": ")
                    .Append(request.Method).Append(' ').Append(request.RequestUri).Append(" ===\n");
                foreach (var (name, value) in headers)
                {
                    transcript.Append(name).Append(": ").Append(Describe(name, value)).Append('\n');
                }

                transcript.Append(body).Append("\n\n");

                if (request.RequestUri!.Host == "api.anthropic.com")
                {
                    return Json(scenario.AnthropicAnswers.Dequeue());
                }

                var rpc = JsonNode.Parse(body)!;
                var method = rpc["method"]!.GetValue<string>();
                if (method == "initialize")
                {
                    var initialized = Json(
                        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"golden-mcp","version":"1.0.0"}}}""");
                    initialized.Headers.Add("Mcp-Session-Id", "golden-session-1");
                    return initialized;
                }

                return scenario.McpAnswers.TryGetValue(method, out var answer)
                    ? answer(rpc)
                    : new HttpResponseMessage(HttpStatusCode.Accepted);
            }
        }
    }
}

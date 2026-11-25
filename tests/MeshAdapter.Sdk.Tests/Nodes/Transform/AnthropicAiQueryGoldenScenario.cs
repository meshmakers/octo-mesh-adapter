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
///     One recorded run of <c>AnthropicAiQuery@1</c> and the three pipeline shapes that are recorded:
///     the data and configuration the node runs on, a scripted Anthropic API and MCP server, the
///     transcript of everything sent through them, and the comparison with the golden file. Shared by
///     the node's golden request tests and by the integration test that runs the same shapes as
///     pipelines through the real orchestrator, so both compare with the same files.
/// </summary>
internal sealed class AnthropicAiQueryGoldenScenario(string dataJson)
{
    internal const string AiConfigName = "AnthropicAiConfig";
    internal const string ServiceAccountConfigName = "McpServiceAccount";
    internal const string TemplateName = "knowledge-ingest";
    internal const string Tenant = "goldenTenant";
    private const string ApiKey = "test-key-golden-0001";
    internal const string DelegatedToken = "test-token-delegated-0002";
    internal const string CallerToken = "test-token-caller-0003";
    private const string McpServerUrl = "https://mcp.example.test";
    private const string SealedApiKey = "test-key-sealed-golden-0004";

    /// <summary>The pipeline service account's token for the central-key LLM gateway (AB#8691).</summary>
    internal const string GatewayToken = "test-token-gateway-0005";

    /// <summary>The AI service the central-key runs reach the gateway at.</summary>
    internal const string GatewayBaseUrl = "https://ai.example.test";

    /// <summary>The sealed endpoint of the OpenAI-compatible runs.</summary>
    internal const string OpenAiBaseUrl = "https://llm.example.test/v1";

    /// <summary>Document analysis: a JSON answer without a schema.</summary>
    internal static (AnthropicAiQueryGoldenScenario Scenario, AnthropicAiQueryNodeConfiguration Config)
        DocumentAnalysis()
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

        var scenario = new AnthropicAiQueryGoldenScenario(
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

        return (scenario, config);
    }

    /// <summary>Chat with MCP tools on the caller's identity, with history and one tool round.</summary>
    internal static (AnthropicAiQueryGoldenScenario Scenario, AnthropicAiQueryNodeConfiguration Config)
        ChatWithMcpTools()
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

        var scenario = new AnthropicAiQueryGoldenScenario(
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

        return (scenario, config);
    }

    /// <summary>Knowledge generation from a prompt template: schema, cached source and a repair call.</summary>
    internal static (AnthropicAiQueryGoldenScenario Scenario, AnthropicAiQueryNodeConfiguration Config)
        KnowledgeGeneration()
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

        var scenario = new AnthropicAiQueryGoldenScenario(
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

        return (scenario, config);
    }

    /// <summary>
    ///     Turns the scenario into an <c>LlmQuery@1</c> run with provider <c>OpenAiCompatible</c>: the
    ///     AiConfiguration gets the sealed endpoint <see cref="OpenAiBaseUrl" /> with its own key, and the scripted
    ///     provider answers there with the given Chat Completions answers instead of the Anthropic ones.
    /// </summary>
    internal void UseOpenAiCompatibleEndpoint(params string[] answers)
    {
        var aiConfiguration = JsonNode.Parse(Configuration[AiConfigName].Json)!.AsObject();
        aiConfiguration["providerEndpoint"] =
            new JsonObject { ["baseUrl"] = OpenAiBaseUrl, ["apiKey"] = SealedApiKey }.ToJsonString();
        Configuration[AiConfigName] = (aiConfiguration.ToJsonString(), null);
        ProviderHost = new Uri(OpenAiBaseUrl).Host;
        AnthropicAnswers.Clear();
        foreach (var answer in answers)
        {
            AnthropicAnswers.Enqueue(answer);
        }
    }

    /// <summary>A Chat Completions answer with text.</summary>
    internal static string OpenAiText(string text, int prompt, int completion, int cached = 0) =>
        new JsonObject
        {
            ["id"] = "chatcmpl-golden",
            ["object"] = "chat.completion",
            ["created"] = 1,
            ["model"] = "gpt-golden",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = "stop"
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completion,
                ["total_tokens"] = prompt + completion,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached }
            }
        }.ToJsonString();

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

    private readonly StringBuilder _transcript = new();
    private int _requestNumber;
    private string? _finalData;

    public Dictionary<string, (string Json, string? Type)> Configuration { get; } = new();
    public Queue<string> AnthropicAnswers { get; } = new();
    public Dictionary<string, Func<JsonNode, HttpResponseMessage>> McpAnswers { get; } = new();
    public VerifiedPrincipal? Principal { get; set; }

    /// <summary>The host the scripted provider answers on; every other host is the MCP server.</summary>
    public string ProviderHost { get; set; } = "api.anthropic.com";

    /// <summary>Every request sent so far, as the golden files record them.</summary>
    public string Transcript => _transcript.ToString();
    public string? CallerAccessToken { get; set; }

    /// <summary>The data the run starts with.</summary>
    public string DataJson => dataJson;

    /// <summary>The configuration hand-over of the pipeline, as the node reads it.</summary>
    public IGlobalConfiguration CreateGlobalConfiguration()
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
        return globalConfiguration;
    }

    /// <summary>
    ///     The scripted Anthropic API and MCP server. Every request sent through the handler is added to
    ///     the transcript.
    /// </summary>
    public HttpMessageHandler CreateWire() => new Wire(this);

    /// <summary>The data left after a run that did not go through <see cref="RunAsync" />.</summary>
    public void SetFinalData(string? finalData) => _finalData = finalData;

    /// <summary>Runs the node directly, with a real data context and node context.</summary>
    public Task RunAsync(AnthropicAiQueryNodeConfiguration config)
    {
        return RunNodeAsync(config, "AnthropicAiQueryNode",
            (etlContext, factory, tokenService) =>
                new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory, tokenService));
    }

    /// <summary>
    ///     Runs the node with <c>keySource: Central</c> (AB#8691): the provider is the gateway at
    ///     <see cref="GatewayBaseUrl" />, the pipeline's gateway token is <see cref="GatewayToken" />.
    /// </summary>
    public Task RunCentralAsync(AnthropicAiQueryNodeConfiguration config)
    {
        ProviderHost = new Uri(GatewayBaseUrl).Host;
        var options = Microsoft.Extensions.Options.Options.Create(
            new Meshmakers.Octo.Sdk.MeshAdapter.Configuration.MeshAdapterConfiguration
            {
                LlmGatewayUrl = GatewayBaseUrl
            });
        return RunNodeAsync(config with { KeySource = LlmKeySource.Central }, "AnthropicAiQueryNode",
            (etlContext, factory, tokenService) =>
            {
                A.CallTo(() => etlContext.GetLlmGatewayTokenAsync())
                    .Returns(ValueTask.FromResult<string?>(GatewayToken));
                return new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory, tokenService, options);
            });
    }

    /// <summary>
    ///     Runs <c>LlmQuery@1</c> with the same settings as the given <c>AnthropicAiQuery@1</c> configuration
    ///     (see <see cref="ToLlmQuery" />), on the same scripted wire.
    /// </summary>
    public Task RunLlmQueryAsync(AnthropicAiQueryNodeConfiguration config,
        LlmProvider provider = LlmProvider.Anthropic)
    {
        return RunNodeAsync(ToLlmQuery(config) with { Provider = provider }, "LlmQueryNode",
            (etlContext, factory, tokenService) =>
                new LlmQueryNode(A.Fake<NodeDelegate>(), etlContext, factory, tokenService));
    }

    /// <summary>
    ///     The <c>LlmQuery@1</c> configuration of a pipeline that moves from <c>AnthropicAiQuery@1</c>: every
    ///     value as it was, the delegation flag as <c>identity: Caller</c>.
    /// </summary>
    internal static LlmQueryNodeConfiguration ToLlmQuery(AnthropicAiQueryNodeConfiguration config)
    {
        Assert.Null(config.ApiKey);
        return new LlmQueryNodeConfiguration
        {
            Path = config.Path,
            TargetPath = config.TargetPath,
            ApiKeyConfigurationName = config.ApiKeyConfigurationName,
            Model = config.Model,
            PromptTemplateName = config.PromptTemplateName,
            Question = config.Question,
            SystemPrompt = config.SystemPrompt,
            DataPaths = config.DataPaths,
            ConversationHistoryPath = config.ConversationHistoryPath,
            MaxTokens = config.MaxTokens,
            Temperature = config.Temperature,
            ResponseFormat = config.ResponseFormat,
            IncludeRawResponse = config.IncludeRawResponse,
            RawResponseOutputPath = config.RawResponseOutputPath,
            JsonFormatSample = config.JsonFormatSample,
            JsonSchema = config.JsonSchema,
            MaxJsonRepairAttempts = config.MaxJsonRepairAttempts,
            UsageOutputPath = config.UsageOutputPath,
            CacheSource = config.CacheSource,
            ContinueOnError = config.ContinueOnError,
            McpServerUrl = config.McpServerUrl,
            McpServiceAccountConfigName = config.McpServiceAccountConfigName,
            Identity = config.DelegatesToCaller ? NodeExecutionIdentity.Caller : config.Identity,
            McpToolNames = config.McpToolNames,
            MaxToolRounds = config.MaxToolRounds
        };
    }

    private async Task RunNodeAsync(INodeConfiguration config, string nodeName,
        Func<IMeshEtlContext, IHttpClientFactory, IServiceAccountTokenService, IPipelineNode> createNode)
    {
        var tenantRepository = A.Fake<ITenantRepository>();
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns(Tenant);
        A.CallTo(() => etlContext.TenantRepository).Returns(tenantRepository);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(CreateGlobalConfiguration());
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(Principal);
        A.CallTo(() => etlContext.CallerAccessToken).Returns(CallerAccessToken);

        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireDelegatedTokenAsync(tenantRepository, ServiceAccountConfigName,
                CallerToken, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(DelegatedToken));

        var handler = CreateWire();
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler, false));

        using var document = JsonDocument.Parse(dataJson);
        using var dataContext = new DataContextImpl(document.RootElement);
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                A.Fake<IPipelineLogger>(), dataContext)
            .RegisterChildNode(nodeName, 0, config, dataContext);

        var node = createNode(etlContext, factory, tokenService);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        _finalData = dataContext.Get<JsonNode>("$")?.ToJsonString();
    }

    public void AssertMatchesGolden(string name)
    {
        AssertMatchesGolden("AnthropicAiQuery", name, mayRecord: true);
    }

    /// <summary>
    ///     Compares the run with a golden file of the given folder. Only a caller that owns the folder may
    ///     record it: the <c>LlmQuery@1</c> parity tests read the files of <c>AnthropicAiQuery@1</c> and must
    ///     never rewrite them.
    /// </summary>
    public void AssertMatchesGolden(string folder, string name, bool mayRecord)
    {
        var (actual, expected) = ReadGolden(folder, name, mayRecord);
        if (expected != null)
        {
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    ///     The comparison of the OpenAI-compatible goldens. Their requests are written by the OpenAI library, so
    ///     they are compared by meaning, not byte for byte: request lines in order, every JSON body as JSON
    ///     (<see cref="JsonNode.DeepEquals" />: member order and spacing do not count, every value does), the
    ///     headers of the provider requests by an allowlist (<see cref="ProviderHeaders" />; the credential as
    ///     its placeholder), every other request with all its headers, and the data context after the node.
    ///     A new library version that only reorders members needs no new recording; one that adds or changes a
    ///     member fails here. The Anthropic goldens stay byte for byte.
    /// </summary>
    public void AssertMatchesGoldenByMeaning(string folder, string name)
    {
        var (actual, expected) = ReadGolden(folder, name, mayRecord: true);
        if (expected == null)
        {
            return;
        }

        var actualSections = ParseTranscript(actual);
        var expectedSections = ParseTranscript(expected);
        Assert.Equal(expectedSections.Select(r => r.Line), actualSections.Select(r => r.Line));
        for (var i = 0; i < expectedSections.Count; i++)
        {
            var (line, expectedHeaders, expectedBody) = expectedSections[i];
            var (_, actualHeaders, actualBody) = actualSections[i];
            var toProvider = line.Contains($"://{ProviderHost}/", StringComparison.Ordinal);
            Assert.Equal(Select(expectedHeaders, toProvider), Select(actualHeaders, toProvider));
            Assert.True(SameJson(expectedBody, actualBody),
                $"Section {i + 1} ({line}) differs.\nexpected: {expectedBody}\nactual:   {actualBody}");
        }

        static IEnumerable<string> Select(List<string> headers, bool toProvider) =>
            toProvider
                ? headers.Where(h => ProviderHeaders.Any(p => h.StartsWith(p + ": ", StringComparison.Ordinal)))
                : headers;

        static bool SameJson(string expected, string actual)
        {
            try
            {
                return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
            }
            catch (JsonException)
            {
                return expected == actual;
            }
        }
    }

    /// <summary>The headers of a provider request the OpenAI goldens compare.</summary>
    internal static readonly string[] ProviderHeaders = ["authorization", "content-type", "user-agent"];

    /// <summary>One section of a transcript: the request line (or the data context), its headers and its body.</summary>
    private static List<(string Line, List<string> Headers, string Body)> ParseTranscript(string transcript)
    {
        var sections = new List<(string Line, List<string> Headers, string Body)>();
        foreach (var block in transcript.Split("=== ", StringSplitOptions.RemoveEmptyEntries))
        {
            // A request is "line\nheaders…\nbody\n\n", the data context "line\njson\n"; the body may be empty.
            var lines = block.TrimEnd('\n').Split('\n');
            if (lines[0].StartsWith("data context", StringComparison.Ordinal))
            {
                sections.Add((lines[0], [], string.Join("\n", lines[1..])));
                continue;
            }

            var withEmptyBody = block.EndsWith("\n\n\n", StringComparison.Ordinal);
            var body = withEmptyBody ? string.Empty : lines[^1];
            var headers = lines[1..(withEmptyBody ? lines.Length : lines.Length - 1)].ToList();
            sections.Add((lines[0], headers, body));
        }

        return sections;
    }

    private (string Actual, string? Expected) ReadGolden(string folder, string name, bool mayRecord)
    {
        var actual = _transcript + "=== data context after the node ===\n" + _finalData + "\n";
        var relativePath = Path.Combine("Resources", "GoldenRequests", folder, name + ".txt");

        if (mayRecord && Environment.GetEnvironmentVariable("OCTO_UPDATE_GOLDEN_REQUESTS") == "1")
        {
            // Nodes/Transform/<this file> → the unit test project folder, whichever project compiled the file.
            var projectDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "..", ".."));
            var target = Path.Combine(projectDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, actual, new UTF8Encoding(false));
            return (actual, null);
        }

        var goldenPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        Assert.True(File.Exists(goldenPath),
            $"Golden file '{relativePath}' is missing. Record it with OCTO_UPDATE_GOLDEN_REQUESTS=1.");

        // A checkout may convert the line ends of the file; the request bodies are single lines.
        return (actual, File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n"));
    }

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

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
            "Bearer " + SealedApiKey => "Bearer <the key of the sealed provider endpoint>",
            "Bearer " + DelegatedToken => "Bearer <the delegated token of the caller>",
            "Bearer " + CallerToken => "Bearer <THE CALLER'S OWN TOKEN - must never be sent>",
            "Bearer " + GatewayToken => "Bearer <the gateway token of the pipeline service account>",
            _ => "<AN UNEXPECTED CREDENTIAL>"
        };
    }

    private sealed class Wire(AnthropicAiQueryGoldenScenario scenario) : HttpMessageHandler
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

            if (request.RequestUri!.Host == scenario.ProviderHost)
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

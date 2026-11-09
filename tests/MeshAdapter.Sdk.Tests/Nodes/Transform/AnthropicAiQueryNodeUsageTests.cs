using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     Usage output and source caching of <c>AnthropicAiQuery@1</c> (AB#8658). The Anthropic API is mocked:
///     the responses carry the <c>usage</c> block the real API returns, so no paid call is made. Whether the
///     API really serves a cache hit is the API's business; these tests pin that the node sends the
///     breakpoint where the hit can happen and reports what the API says.
/// </summary>
public class AnthropicAiQueryNodeUsageTests : NodeTestBase
{
    private const string Source = "LARGE SOURCE DOCUMENT";

    private sealed class ScriptedAnthropic(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<JsonNode> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            var body = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Answer(string text, int input, int output, int cacheRead = 0, int cacheCreation = 0,
        string stopReason = "end_turn") =>
        new JsonObject
        {
            ["stop_reason"] = stopReason,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["output_tokens"] = output,
                ["cache_read_input_tokens"] = cacheRead,
                ["cache_creation_input_tokens"] = cacheCreation
            }
        }.ToJsonString();

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, ScriptedAnthropic Api,
        DataContextWrites Writes) Prepare(AnthropicAiQueryNodeConfiguration config,
            params string[] responses)
    {
        var api = new ScriptedAnthropic(responses);
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(api, false));

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());

        var (dataContext, nodeContext, _) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns(Source);

        var writes = new DataContextWrites(dataContext);

        var node = new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory,
            A.Fake<IServiceAccountTokenService>(), A.Fake<IServiceClientAccessToken>());
        return (node, dataContext, nodeContext, api, writes);
    }

    private static AnthropicAiQueryNodeConfiguration Config() => new()
    {
        ApiKey = "test-key",
        Model = "claude-sonnet-4-6",
        Question = "What is it about?",
        Path = "$.text",
        TargetPath = "$.out",
        UsageOutputPath = "$.usage"
    };

    private static JsonObject UsageWritten(DataContextWrites writes) =>
        Assert.IsType<JsonObject>(Assert.Single(writes.All, w => w.Path == "$.usage").Value);

    // ---- AC4: usage and cost estimate ----

    [Fact]
    public async Task Usage_IsWrittenWithAllTokenCountsAndACostEstimate()
    {
        var (node, dataContext, nodeContext, _, writes) =
            Prepare(Config(), Answer("""{"a":1}""", input: 1200, output: 300, cacheRead: 5000, cacheCreation: 800));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var usage = UsageWritten(writes);
        Assert.Equal("claude-sonnet-4-6", usage["model"]!.GetValue<string>());
        Assert.Equal(1, usage["calls"]!.GetValue<int>());
        Assert.Equal(1200, usage["inputTokens"]!.GetValue<long>());
        Assert.Equal(300, usage["outputTokens"]!.GetValue<long>());
        Assert.Equal(5000, usage["cacheReadInputTokens"]!.GetValue<long>());
        Assert.Equal(800, usage["cacheCreationInputTokens"]!.GetValue<long>());
        // Sonnet 4.6: $3 in, $15 out, cache read 0.1x = $0.30, cache write 1.25x = $3.75 per MTok.
        // 1200*3 + 300*15 + 5000*0.30 + 800*3.75 = 3600 + 4500 + 1500 + 3000 = 12600 µ$ = 0.0126 $.
        Assert.Equal(0.0126m, usage["estimatedCostUsd"]!.GetValue<decimal>());
        Assert.Equal(AnthropicPricing.PriceListDate, usage["priceListDate"]!.GetValue<string>());
    }

    [Fact]
    public async Task Usage_IsReadableFromARealDataContext()
    {
        using var document = JsonDocument.Parse($$$"""{"text":"{{{Source}}}"}""");
        using var dataContext = new DataContextImpl(document.RootElement);
        var config = Config();
        var nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
                A.Fake<IPipelineLogger>(), dataContext)
            .RegisterChildNode("AnthropicAiQueryNode", 0, config, dataContext);

        var api = new ScriptedAnthropic(Answer("""{"a":1}""", input: 1200, output: 300, cacheRead: 5000));
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(api, false));
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(A.Fake<IGlobalConfiguration>());
        var node = new AnthropicAiQueryNode(A.Fake<NodeDelegate>(), etlContext, factory,
            A.Fake<IServiceAccountTokenService>(), A.Fake<IServiceClientAccessToken>());

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(1200, dataContext.Get<long>("$.usage.inputTokens"));
        Assert.Equal(5000, dataContext.Get<long>("$.usage.cacheReadInputTokens"));
        Assert.Equal(1, dataContext.Get<int>("$.out.a"));
    }

    [Fact]
    public async Task Usage_SumsEveryCall_IncludingJsonRepairCalls()
    {
        var config = Config() with
        {
            JsonSchema = """{"type":"object","properties":{"a":{"type":"integer"}},"required":["a"]}"""
        };
        var (node, dataContext, nodeContext, api, writes) = Prepare(config,
            Answer("""{"b":1}""", input: 1000, output: 100),
            Answer("""{"a":1}""", input: 200, output: 20));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, api.Requests.Count);
        var usage = UsageWritten(writes);
        Assert.Equal(2, usage["calls"]!.GetValue<int>());
        Assert.Equal(1200, usage["inputTokens"]!.GetValue<long>());
        Assert.Equal(120, usage["outputTokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task UnknownModel_WritesTokensWithoutACostEstimate()
    {
        var (node, dataContext, nodeContext, _, writes) =
            Prepare(Config() with { Model = "some-future-model" }, Answer("""{"a":1}""", input: 10, output: 5));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var usage = UsageWritten(writes);
        Assert.Equal(10, usage["inputTokens"]!.GetValue<long>());
        Assert.Null(usage["estimatedCostUsd"]);
    }

    [Fact]
    public async Task WithoutUsageOutputPath_NothingIsWritten()
    {
        var (node, dataContext, nodeContext, _, writes) =
            Prepare(Config() with { UsageOutputPath = null }, Answer("""{"a":1}""", input: 10, output: 5));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(["$.out"], writes.All.Select(w => w.Path));
    }

    [Fact]
    public async Task FailedQuery_WithContinueOnError_StillWritesTheUsageOfThePaidCall()
    {
        var (node, dataContext, nodeContext, _, writes) = Prepare(Config() with { ContinueOnError = true },
            Answer("cut", input: 900, output: 1000, stopReason: "max_tokens"));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(1000, UsageWritten(writes)["outputTokens"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4.0, 20.0, 0.20)]
    [InlineData("claude-sonnet-4-6-20260101", 3.0, 15.0, 0.30)]
    [InlineData("claude-haiku-4-5", 1.0, 5.0, 0.10)]
    [InlineData("claude-fable-5-1", 10.0, 50.0, 0.25)]
    [InlineData("claude-fable-5", 10.0, 50.0, 1.00)]
    public void PriceTable_LongestPrefixWins(string model, double input, double output, double cacheRead)
    {
        var price = AnthropicPricing.Find(model);

        Assert.NotNull(price);
        Assert.Equal((decimal)input, price!.InputPerMTok);
        Assert.Equal((decimal)output, price.OutputPerMTok);
        Assert.Equal((decimal)cacheRead, price.CacheReadPerMTok);
        Assert.Equal((decimal)input * 1.25m, price.CacheWrite5mPerMTok);
    }

    [Fact]
    public void Accumulator_ResponseWithoutUsage_CountsTheCallOnly()
    {
        var usage = new AiUsageAccumulator();
        usage.Add(JsonSerializer.Deserialize<JsonElement>("""{"stop_reason":"end_turn"}"""));

        Assert.Equal(1, usage.Calls);
        Assert.Equal(0, usage.InputTokens);
    }

    // ---- AC5: source caching ----

    [Fact]
    public async Task CacheSource_PutsTheSourceIntoItsOwnBlockWithABreakpoint_AndASecondCallReadsTheCache()
    {
        var config = Config() with { CacheSource = true };
        var first = Prepare(config, Answer("""{"a":1}""", input: 40, output: 10, cacheCreation: 6000));
        var second = Prepare(config, Answer("""{"a":1}""", input: 40, output: 10, cacheRead: 6000));

        await first.Node.ProcessObjectAsync(first.DataContext, first.NodeContext);
        await second.Node.ProcessObjectAsync(second.DataContext, second.NodeContext);

        var content = first.Api.Requests[0]["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Contains(Source, content[0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", content[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("What is it about?", content[0]!["text"]!.GetValue<string>());
        Assert.Contains("What is it about?", content[1]!["text"]!.GetValue<string>());
        Assert.Null(content[1]!["cache_control"]);

        // The cached prefix is byte-identical between the two calls — the precondition of a hit.
        Assert.Equal(first.Api.Requests[0].ToJsonString(), second.Api.Requests[0].ToJsonString());
        Assert.Equal(6000, UsageWritten(first.Writes)["cacheCreationInputTokens"]!.GetValue<long>());
        Assert.True(UsageWritten(second.Writes)["cacheReadInputTokens"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task CacheSource_AChangedQuestionAndDraftInDataPaths_KeepTheCachedBlockIdentical()
    {
        var generate = Prepare(Config() with { CacheSource = true }, Answer("""{"a":1}""", 1, 1));
        var verify = Prepare(Config() with
        {
            CacheSource = true,
            Question = "Is the draft correct?",
            DataPaths = ["$.draft"]
        }, Answer("""{"a":1}""", 1, 1));
        A.CallTo(() => verify.DataContext.GetKind("$.draft")).Returns(DataKind.String);
        A.CallTo(() => verify.DataContext.Get<string>("$.draft")).Returns("DRAFT PAGE");

        await generate.Node.ProcessObjectAsync(generate.DataContext, generate.NodeContext);
        await verify.Node.ProcessObjectAsync(verify.DataContext, verify.NodeContext);

        static JsonArray Blocks(JsonNode request) => request["messages"]!.AsArray().Last()!["content"]!.AsArray();

        // The changing parts (draft, question) sit after the breakpoint, so the cached block is identical.
        Assert.Equal(Blocks(generate.Api.Requests[0])[0]!.ToJsonString(), Blocks(verify.Api.Requests[0])[0]!.ToJsonString());
        Assert.DoesNotContain("DRAFT PAGE", Blocks(verify.Api.Requests[0])[0]!["text"]!.GetValue<string>());
        Assert.Contains("DRAFT PAGE", Blocks(verify.Api.Requests[0])[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void PromptParts_ConcatenateToTheUnchangedPrompt_ForAnySplit()
    {
        const string context = "Main Content:\nM\n\nAdditional Context:\nX\n";
        var full = AnthropicAiQueryNode.BuildUserPrompt("Q?", context, "json", "{}");

        foreach (var split in new[] { 0, 5, 16, context.Length })
        {
            var (source, instruction) =
                AnthropicAiQueryNode.BuildUserPromptParts("Q?", context, "json", "{}", null, split);
            Assert.Equal(full, source + instruction);
        }
    }

    [Fact]
    public async Task CacheSource_TheModelReadsTheSameTextAsWithoutIt()
    {
        var cached = Prepare(Config() with { CacheSource = true }, Answer("""{"a":1}""", 1, 1));
        var plain = Prepare(Config(), Answer("""{"a":1}""", 1, 1));

        await cached.Node.ProcessObjectAsync(cached.DataContext, cached.NodeContext);
        await plain.Node.ProcessObjectAsync(plain.DataContext, plain.NodeContext);

        var blocks = cached.Api.Requests[0]["messages"]!.AsArray().Last()!["content"]!.AsArray();
        var joined = string.Concat(blocks.Select(b => b!["text"]!.GetValue<string>()));
        Assert.Equal(plain.Api.Requests[0]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>(), joined);
    }

    [Fact]
    public async Task WithoutCacheSource_TheUserMessageIsAPlainString()
    {
        var (node, dataContext, nodeContext, api, _) = Prepare(Config(), Answer("""{"a":1}""", 1, 1));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(JsonValueKind.String,
            api.Requests[0]["messages"]!.AsArray().Last()!["content"]!.GetValueKind());
    }
}

using System.Diagnostics;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
/// Spike 2 / Phase E smoke test: exercises <see cref="LlmQueryNode"/> end-to-end
/// against a local Ollama instance running model <c>nemotron-3-nano:4b</c>.
///
/// <para>
/// This is a deliberately tiny round-trip — it proves the provider-agnostic
/// path (config -&gt; IChatClient -&gt; OpenAI-compatible HTTP) works against a
/// real backend with no MeshAdapter host, no MongoDB, and no Studio deploy in
/// the loop. Sister test for the Anthropic native branch will land in Spike 4.
/// </para>
///
/// <para>
/// Prerequisites:
/// <list type="bullet">
///   <item><description>Ollama running at http://localhost:11434</description></item>
///   <item><description><c>ollama pull nemotron-3-nano:4b</c> done</description></item>
/// </list>
/// CI must filter out this trait until a network-capable runner exists:
/// <c>dotnet test --filter "Category!=RequiresOllama"</c>.
/// </para>
/// </summary>
[Trait("Category", "RequiresOllama")]
public class LlmQuerySmokeTests
{
    private const string OllamaBaseUrl = "http://localhost:11434/v1/";
    private const string Model = "nemotron-3-nano:4b";

    private readonly ITestOutputHelper _output;

    public LlmQuerySmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ProcessObjectAsync_AgainstLocalOllama_ReturnsNonEmptyResponse()
    {
        await EnsureOllamaReachableAsync();

        // ---------------- Arrange ----------------

        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.OpenAiCompatible,
            BaseUrl = OllamaBaseUrl,
            ApiKey = "ollama", // any non-empty string — Ollama ignores it
            Model = Model,

            Question = "Summarize the following text in exactly one sentence.",
            SystemPrompt = "You are a concise summarizer. Reply with one sentence only.",

            ResponseFormat = "text",
            MaxTokens = 120,
            Temperature = 0.2,
            TimeoutSeconds = 60,

            Path = "$.text",
            TargetPath = "$.summary",

            ContinueOnError = false,
            IncludeRawResponse = false
        };

        var input = new JObject
        {
            ["text"] =
                "OctoMesh is a knowledge-graph platform that turns telemetry, " +
                "documents, and operational data into a queryable graph of " +
                "real-time entities, connected through pipelines that extract, " +
                "transform, and load data from heterogeneous sources."
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, input);

        // Capture whatever the node writes back, regardless of overload.
        object? capturedValue = null;
        string? capturedPath = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == "Set")
            .Invokes(call =>
            {
                capturedPath = call.Arguments[0] as string;
                capturedValue = call.Arguments[1];
            });

        var etlContext = A.Fake<IMeshEtlContext>();
        var node = new LlmQueryNode(next, etlContext);

        // ---------------- Act ----------------

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // ---------------- Assert ----------------

        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();

        capturedPath.Should().Be("$.summary",
            "the node should have written to the configured TargetPath");

        capturedValue.Should().NotBeNull(
            "Ollama should have returned a non-empty response");

        var responseText = capturedValue!.ToString();
        responseText.Should().NotBeNullOrWhiteSpace();

        // Surface the actual model output in the xUnit test log — this is the
        // evidence we capture for the Spike 2 / Phase E write-up.
        _output.WriteLine($"Model: {Model}");
        _output.WriteLine($"TargetPath: {capturedPath}");
        _output.WriteLine($"Response ({responseText!.Length} chars):");
        _output.WriteLine(responseText);
    }

    [Fact]
    public async Task ProcessObjectAsync_AgainstLocalOllama_EmitsGenAiActivityTags()
    {
        await EnsureOllamaReachableAsync();

        // ---------------- Arrange ----------------

        // Register an in-process ActivityListener filtered to the well-known
        // source name emitted by LlmQueryNode via .UseOpenTelemetry(). This is
        // the same source name operators would AddSource(...) in production
        // when wiring Tempo/Grafana — so this test is the contract that
        // protects that wiring from regressing.
        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LlmQueryNode.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _)
                => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _)
                => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => capturedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.OpenAiCompatible,
            BaseUrl = OllamaBaseUrl,
            ApiKey = "ollama",
            Model = Model,

            Question = "What is 2 + 2? Answer with the number only.",
            SystemPrompt = "You are a calculator. Answer with the number only.",

            ResponseFormat = "text",
            MaxTokens = 20,
            Temperature = 0.0,
            TimeoutSeconds = 60,

            Path = "$.text",
            TargetPath = "$.answer",

            ContinueOnError = false
        };

        var input = new JObject { ["text"] = "two plus two" };

        var (dataContext, nodeContext, next) = PrepareTest(config, input);
        var etlContext = A.Fake<IMeshEtlContext>();
        var node = new LlmQueryNode(next, etlContext);

        // ---------------- Act ----------------

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // ---------------- Assert ----------------

        capturedActivities.Should().NotBeEmpty(
            $"the node should have emitted at least one activity on " +
            $"source '{LlmQueryNode.ActivitySourceName}' " +
            "(check that .UseOpenTelemetry() is wired in ConstructOpenAiCompatibleClient)");

        // The chat activity is the one we care about. There may be additional
        // child activities (e.g. HTTP) but the gen_ai.* tags live on the chat span.
        var chatActivity = capturedActivities
            .FirstOrDefault(a => a.Tags.Any(t => t.Key.StartsWith("gen_ai.", StringComparison.Ordinal)))
            ?? capturedActivities[0];

        // Dump every tag for evidence (and so a future tag-rename in
        // Microsoft.Extensions.AI.OpenTelemetry surfaces visibly in CI logs).
        _output.WriteLine($"Activity source: {LlmQueryNode.ActivitySourceName}");
        _output.WriteLine($"Activity name:   {chatActivity.OperationName}");
        _output.WriteLine($"Activity status: {chatActivity.Status}");
        _output.WriteLine($"Duration:        {chatActivity.Duration.TotalMilliseconds:F0} ms");
        _output.WriteLine($"Captured {capturedActivities.Count} activity/activities; tags on the chat span:");
        foreach (var tag in chatActivity.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {tag.Key} = {tag.Value}");
        }

        // Stable activity-tag assertions, matched against MEAI 10.6.0's actual
        // emission (verified empirically — see the tag dump above). The OTel
        // GenAI semconv was renamed: gen_ai.system -> gen_ai.provider.name.
        // If a future MEAI version renames again, the tag dump in the test
        // log surfaces the new name immediately.

        chatActivity.OperationName.Should().Be($"chat {Model}",
            "OTel GenAI semconv prescribes `chat <model>` as the span name");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.operation.name" && t.Value == "chat",
            "the chat operation kind must be tagged so dashboards can filter");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.provider.name" && t.Value == "openai",
            "all OpenAI-compatible backends report 'openai' as the provider " +
            "(provider-agnostic telemetry; the real backend lives in " +
            "server.address and openai.response.system_fingerprint)");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.request.model" && t.Value == Model,
            "the requested model must round-trip into the trace");

        chatActivity.Tags.Should().Contain(t => t.Key == "gen_ai.response.model",
            "the actually-served model must be visible (it can differ from " +
            "the requested one when a backend falls back or aliases)");

        chatActivity.Tags.Should().Contain(t => t.Key == "server.address",
            "server.address pins which backend handled the call — the only " +
            "way to tell Ollama / vLLM / Cerebras / OpenAI cloud apart " +
            "given gen_ai.provider.name is always 'openai' here");

        // Note on token usage: OTel GenAI semconv emits prompt/completion
        // token counts as METRICS (gen_ai.client.token.usage histogram),
        // not as activity tags. Spike 6 (Octo.AIServices metering) needs a
        // MeterListener for that channel — outside the scope of this smoke
        // test. We assert here only the activity-tag contract.
    }

    [Fact]
    public async Task ProcessObjectAsync_WithShortTimeout_PropagatesOperationCanceledException()
    {
        await EnsureOllamaReachableAsync();

        // The node uses CancellationTokenSource(config.Timeout) and passes the
        // token to IChatClient.GetResponseAsync. The catch block deliberately
        // rethrows OperationCanceledException so that the pipeline runtime sees
        // cancellation cleanly, REGARDLESS of ContinueOnError — that's the
        // contract this test pins down.
        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.OpenAiCompatible,
            BaseUrl = OllamaBaseUrl,
            ApiKey = "ollama",
            Model = Model,

            // Force a non-trivial response so we're confident the call hasn't
            // already completed by the time the CTS fires.
            Question = "Write a 500-word essay about distributed systems.",
            SystemPrompt = "You are a verbose technical writer. Be thorough.",
            ResponseFormat = "text",
            MaxTokens = 800,
            Temperature = 0.7,

            // The whole point of the test — fire cancellation well before the
            // backend can stream a full response. 1 second is comfortably
            // below Ollama's typical response time (5-10s for any meaningful
            // prompt against nemotron-3-nano:4b on consumer hardware).
            TimeoutSeconds = 1,

            Path = "$.text",
            TargetPath = "$.summary",

            // Deliberately TRUE — to prove OCE is rethrown even when the
            // error-swallowing flag is on. Cancellation must never be
            // silently absorbed.
            ContinueOnError = true
        };

        var input = new JObject { ["text"] = "lorem ipsum dolor sit amet" };
        var (dataContext, nodeContext, next) = PrepareTest(config, input);
        var etlContext = A.Fake<IMeshEtlContext>();
        var node = new LlmQueryNode(next, etlContext);

        // ---------------- Act + Assert ----------------

        var act = async () => await node.ProcessObjectAsync(dataContext, nodeContext);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the node must surface OperationCanceledException up the pipeline " +
            "even with ContinueOnError=true — cancellation is never silently swallowed");

        A.CallTo(() => next(dataContext, nodeContext))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_BadModel_WithContinueOnErrorTrue_CallsNextAndSwallowsError()
    {
        await EnsureOllamaReachableAsync();

        const string bogusModel = "this-model-does-not-exist-xyz-9999";

        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.OpenAiCompatible,
            BaseUrl = OllamaBaseUrl,
            ApiKey = "ollama",
            Model = bogusModel,
            Question = "Anything.",
            SystemPrompt = "You are helpful.",
            ResponseFormat = "text",
            MaxTokens = 10,
            Temperature = 0.0,
            TimeoutSeconds = 30,
            Path = "$.text",
            TargetPath = "$.summary",
            ContinueOnError = true   // <- the contract under test
        };

        var input = new JObject { ["text"] = "anything" };
        var (dataContext, nodeContext, next) = PrepareTest(config, input);

        var setValueCalled = false;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == "Set")
            .Invokes(_ => setValueCalled = true);

        var etlContext = A.Fake<IMeshEtlContext>();
        var node = new LlmQueryNode(next, etlContext);

        // ---------------- Act ----------------

        var act = async () => await node.ProcessObjectAsync(dataContext, nodeContext);

        // ---------------- Assert ----------------

        await act.Should().NotThrowAsync(
            "ContinueOnError=true must absorb backend errors and let the pipeline proceed");

        // next() must still be invoked so downstream nodes can react to the
        // missing target value.
        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();

        setValueCalled.Should().BeFalse(
            "the node throws before reaching Set on the data context, so the target " +
            "path must stay untouched — downstream nodes can branch on its absence");
    }

    [Fact]
    public async Task ProcessObjectAsync_BadModel_WithContinueOnErrorFalse_ThrowsPipelineException()
    {
        await EnsureOllamaReachableAsync();

        const string bogusModel = "this-model-does-not-exist-xyz-9999";

        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.OpenAiCompatible,
            BaseUrl = OllamaBaseUrl,
            ApiKey = "ollama",
            Model = bogusModel,
            Question = "Anything.",
            SystemPrompt = "You are helpful.",
            ResponseFormat = "text",
            MaxTokens = 10,
            Temperature = 0.0,
            TimeoutSeconds = 30,
            Path = "$.text",
            TargetPath = "$.summary",
            ContinueOnError = false   // <- the contract under test
        };

        var input = new JObject { ["text"] = "anything" };
        var (dataContext, nodeContext, next) = PrepareTest(config, input);
        var etlContext = A.Fake<IMeshEtlContext>();
        var node = new LlmQueryNode(next, etlContext);

        // ---------------- Act + Assert ----------------

        var act = async () => await node.ProcessObjectAsync(dataContext, nodeContext);

        // The node wraps non-OCE exceptions in MeshAdapterPipelineExecutionException
        // via MeshAdapterPipelineExecutionException.ProcessingError(...). We assert
        // that *some* exception escapes and that it's not OCE (which would mean
        // the cancellation path fired by accident).
        var thrown = await act.Should().ThrowAsync<Exception>(
            "ContinueOnError=false must propagate backend errors so the pipeline halts");

        thrown.Which.Should().NotBeOfType<OperationCanceledException>(
            "this test is about error propagation, not cancellation");

        // With ContinueOnError=false the node throws before reaching the
        // final `await next(...)`, so downstream nodes do not run.
        A.CallTo(() => next(dataContext, nodeContext))
            .MustNotHaveHappened();
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest<TConfig>(
        TConfig config,
        JToken testData)
        where TConfig : class, INodeConfiguration
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        // STJ-era IDataContext has no Current/SelectToken; node code reads via
        // GetKind + Get<string>. Wire both to defer to the testData JToken
        // so existing test fixtures can keep passing JObject literals.
        A.CallTo(() => dataContext.GetKind(A<string>._))
            .ReturnsLazily((string path) =>
            {
                var token = testData.SelectToken(path);
                if (token == null) return DataKind.Undefined;
                return token.Type switch
                {
                    JTokenType.String => DataKind.String,
                    JTokenType.Integer or JTokenType.Float => DataKind.Number,
                    JTokenType.Boolean => DataKind.Boolean,
                    JTokenType.Array => DataKind.Array,
                    JTokenType.Object => DataKind.Object,
                    JTokenType.Null => DataKind.Null,
                    _ => DataKind.Undefined
                };
            });

        A.CallTo(() => dataContext.Get<string>(A<string>._))
            .ReturnsLazily((string path) => testData.SelectToken(path)?.ToString() ?? string.Empty);

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            typeof(TConfig).Name.Replace("Configuration", ""),
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    /// <summary>
    /// Probes the Ollama models endpoint. Fails with a clear message rather
    /// than silently passing or throwing a confusing connection error.
    /// </summary>
    private static async Task EnsureOllamaReachableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var resp = await http.GetAsync($"{OllamaBaseUrl}models");
            resp.IsSuccessStatusCode.Should().BeTrue(
                $"Ollama responded but with status {(int)resp.StatusCode}. " +
                "Ensure the daemon is healthy.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"Ollama is not reachable at {OllamaBaseUrl}. " +
                "Start it with `ollama serve` and pull the model with " +
                $"`ollama pull {Model}` before running this smoke test. " +
                "CI runs should exclude it via `--filter Category!=RequiresOllama`.",
                ex);
        }
    }
}

using System.Diagnostics;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
/// Spike 4 / Phase C smoke tests: exercises <see cref="LlmQueryNode"/> end-to-end
/// against the native Anthropic API via the official <c>Anthropic</c> .NET SDK
/// (v12.17.0+). Sister class to <see cref="LlmQuerySmokeTests"/>, which covers
/// the OpenAI-compatible branch.
///
/// <para>
/// These tests prove the provider-agnostic <see cref="IChatClient"/> contract
/// works identically through the Anthropic-native path, and confirm the
/// <c>gen_ai.provider.name</c> attribute differs ("anthropic" vs "openai") —
/// the central architectural observation of the LlmQuery fork.
/// </para>
///
/// <para>
/// Prerequisites:
/// <list type="bullet">
///   <item><description>Environment variable <c>ANTHROPIC_API_KEY</c>
///     set to a valid key (Console → API Keys)</description></item>
/// </list>
/// CI must filter out this trait until a runner has an Anthropic key:
/// <c>dotnet test --filter "Category!=RequiresAnthropic"</c>.
/// </para>
/// </summary>
[Trait("Category", "RequiresAnthropic")]
public class LlmQueryAnthropicSmokeTests
{
    // claude-haiku-4-5 is the cheapest current production model. Adjust if
    // Anthropic's lineup changes — the strongly-typed Model.* constants
    // in the Anthropic SDK can serve as a discovery reference.
    private const string Model = "claude-haiku-4-5";

    private readonly ITestOutputHelper _output;

    public LlmQueryAnthropicSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ProcessObjectAsync_AgainstAnthropic_ReturnsNonEmptyResponse()
    {
        var apiKey = RequireApiKey();

        // ---------------- Arrange ----------------

        var config = new LlmQueryNodeConfiguration
        {
            Provider = LlmProvider.Anthropic,
            ApiKey = apiKey,
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
        var node = new LlmQueryNode(next, etlContext, A.Fake<IServiceAccountTokenService>());

        // ---------------- Act ----------------

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // ---------------- Assert ----------------

        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();

        capturedPath.Should().Be("$.summary",
            "the node should have written to the configured TargetPath");

        capturedValue.Should().NotBeNull(
            "Anthropic should have returned a non-empty response");

        var responseText = capturedValue!.ToString();
        responseText.Should().NotBeNullOrWhiteSpace();

        _output.WriteLine($"Model: {Model}");
        _output.WriteLine($"TargetPath: {capturedPath}");
        _output.WriteLine($"Response ({responseText!.Length} chars):");
        _output.WriteLine(responseText);
    }

    [Fact]
    public async Task ProcessObjectAsync_AgainstAnthropic_EmitsGenAiActivityTags()
    {
        var apiKey = RequireApiKey();

        // ---------------- Arrange ----------------

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
            Provider = LlmProvider.Anthropic,
            ApiKey = apiKey,
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
        var node = new LlmQueryNode(next, etlContext, A.Fake<IServiceAccountTokenService>());

        // ---------------- Act ----------------

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // ---------------- Assert ----------------

        capturedActivities.Should().NotBeEmpty(
            $"the node should have emitted at least one activity on " +
            $"source '{LlmQueryNode.ActivitySourceName}'");

        var chatActivity = capturedActivities
            .FirstOrDefault(a => a.Tags.Any(t => t.Key.StartsWith("gen_ai.", StringComparison.Ordinal)))
            ?? capturedActivities[0];

        // Dump every tag — the value lives in the test log for spike4
        // evidence. Particularly the bonus Anthropic-specific tags
        // (cache_read.input_tokens, time_to_first_chunk if streaming).
        _output.WriteLine($"Activity source: {LlmQueryNode.ActivitySourceName}");
        _output.WriteLine($"Activity name:   {chatActivity.OperationName}");
        _output.WriteLine($"Activity status: {chatActivity.Status}");
        _output.WriteLine($"Duration:        {chatActivity.Duration.TotalMilliseconds:F0} ms");
        _output.WriteLine($"Captured {capturedActivities.Count} activity/activities; tags on the chat span:");
        foreach (var tag in chatActivity.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {tag.Key} = {tag.Value}");
        }

        // The architectural observation: gen_ai.provider.name differs
        // between the two branches. OpenAI-compat reports "openai",
        // Anthropic-native reports "anthropic". This is the contract
        // surface that lets dashboards distinguish backends.
        chatActivity.OperationName.Should().Be($"chat {Model}",
            "OTel GenAI semconv prescribes `chat <model>` as the span name");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.operation.name" && t.Value == "chat",
            "the chat operation kind must be tagged so dashboards can filter");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.provider.name" && t.Value == "anthropic",
            "this is the headline architectural difference vs the " +
            "OpenAI-compatible path, which reports 'openai' for the same tag");

        chatActivity.Tags.Should().Contain(
            t => t.Key == "gen_ai.request.model" && t.Value == Model,
            "the requested model must round-trip into the trace");

        chatActivity.Tags.Should().Contain(t => t.Key == "gen_ai.response.model",
            "the actually-served model must be visible (Anthropic reports " +
            "the full versioned model id here, e.g. claude-haiku-4-5-20251001)");

        chatActivity.Tags.Should().Contain(t => t.Key == "server.address",
            "server.address pins which backend handled the call");

        chatActivity.Tags.Should().Contain(t => t.Key == "gen_ai.response.id",
            "the per-request id is useful for correlation with Anthropic " +
            "Console logs (Anthropic-native exposes it as a first-class tag)");

        // NOTE on usage tokens: by default MEAI's UseOpenTelemetry()
        // sends gen_ai.usage.* through the *meter* channel, not the
        // *activity* (span) channel — only emitted as span attributes
        // when EnableSensitiveData is true. We leave EnableSensitiveData
        // off in production (don't leak prompts or per-call token
        // counts to traces). For cost-tracking dashboards, subscribe a
        // MeterListener to the gen_ai meter (Spike 6 / Octo.AIServices
        // metering integration). The same applies to Anthropic-specific
        // gen_ai.usage.cache_read.input_tokens — present on the meter,
        // not on the span tags with the default config.
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
    /// Loads the Anthropic API key from environment. Fails with a clear
    /// message if missing rather than throwing a generic 401 deep in the
    /// SDK. CI filter <c>Category!=RequiresAnthropic</c> excludes these
    /// tests on machines/runners that don't have the key.
    /// </summary>
    private static string RequireApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(apiKey),
            "ANTHROPIC_API_KEY not set — skipping Anthropic smoke test. " +
            "Set it locally (`export ANTHROPIC_API_KEY=sk-ant-...`) to run this.");
        return apiKey!;
    }
}

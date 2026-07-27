using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins <see cref="AnthropicAiQueryNode.ResolveMainContent" />, the optional-main-content resolver.
/// Regression: the node's base-class default <c>Path</c> is <c>"$"</c>. Reading the whole root
/// object as a string threw ("Cannot get the value of a token type 'StartObject' as a string"),
/// crashing every MCP-only pipeline (which never sets an explicit path). The resolver must return
/// null for the default root object instead of touching <c>Get&lt;string&gt;</c>.
/// </summary>
public class AnthropicAiQueryNodeTests
{
    private static IDataContext DataContextWith(string path, DataKind kind)
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetKind(path)).Returns(kind);
        return dataContext;
    }

    [Fact]
    public void ResolveMainContent_DefaultRootObject_ReturnsNullWithoutReadingString()
    {
        // MCP-only pipeline: Path defaults to "$", the root is a JSON object.
        var dataContext = DataContextWith("$", DataKind.Object);

        var result = AnthropicAiQueryNode.ResolveMainContent(dataContext, "$");

        Assert.Null(result);
        // Must NOT attempt to read the root object as a string (that was the crash).
        A.CallTo(() => dataContext.Get<string>("$")).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveMainContent_EmptyPath_ReturnsNull(string? path)
    {
        var dataContext = A.Fake<IDataContext>();

        Assert.Null(AnthropicAiQueryNode.ResolveMainContent(dataContext, path));
        A.CallTo(() => dataContext.GetKind(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public void ResolveMainContent_StringAtPath_ReturnsTheString()
    {
        var dataContext = DataContextWith("$.text", DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("hello world");

        Assert.Equal("hello world", AnthropicAiQueryNode.ResolveMainContent(dataContext, "$.text"));
    }

    [Fact]
    public void ResolveMainContent_UndefinedPath_ReturnsNull()
    {
        var dataContext = DataContextWith("$.missing", DataKind.Undefined);

        Assert.Null(AnthropicAiQueryNode.ResolveMainContent(dataContext, "$.missing"));
    }

    [Fact]
    public void ResolveMainContent_NonStringAtExplicitPath_RendersAsJson()
    {
        // A concrete non-string value at an explicit (non-root) path is rendered as JSON for the
        // prompt instead of crashing — same treatment as the DataPaths context values.
        var dataContext = DataContextWith("$.value", DataKind.Number);
        A.CallTo(() => dataContext.Get<object?>("$.value")).Returns(42);

        var result = AnthropicAiQueryNode.ResolveMainContent(dataContext, "$.value");

        Assert.Equal("42", result);
    }

    // ── ExtractJsonFromText: prose/markdown-wrapped JSON recovery ──
    // Regression: a prose-wrapped top-level ARRAY (the mapping-suggestions shape) must be extracted
    // whole. The old extractor only handled '{' objects, so it grabbed the first inner mapping
    // object instead of the array — downstream ForEach then failed with "value is not an array".

    [Fact]
    public void ExtractJsonFromText_ProseWrappedArray_ReturnsWholeArray()
    {
        var text = "Here are the mappings I found:\n[{\"name\":\"a\"},{\"name\":\"b\"}]\nThat's all.";

        var json = AnthropicAiQueryNode.ExtractJsonFromText(text);

        Assert.Equal("[{\"name\":\"a\"},{\"name\":\"b\"}]", json);
    }

    [Fact]
    public void ExtractJsonFromText_FencedJsonArray_ReturnsArray()
    {
        var text = "Result:\n```json\n[{\"x\":1}]\n```\ndone";

        var json = AnthropicAiQueryNode.ExtractJsonFromText(text);

        Assert.Equal("[{\"x\":1}]", json);
    }

    [Fact]
    public void ExtractJsonFromText_ArrayWithBracketsInStringValues_StaysBalanced()
    {
        // Brackets/braces inside a JSON string value must not unbalance the scan.
        var text = "note: [{\"reason\":\"matched [Wohnen] and {closed}\"},{\"reason\":\"ok\"}]";

        var json = AnthropicAiQueryNode.ExtractJsonFromText(text);

        Assert.Equal("[{\"reason\":\"matched [Wohnen] and {closed}\"},{\"reason\":\"ok\"}]", json);
    }

    [Fact]
    public void ExtractJsonFromText_ProseWrappedObject_StillReturnsObject()
    {
        var text = "The answer is {\"a\":1,\"b\":2} exactly.";

        var json = AnthropicAiQueryNode.ExtractJsonFromText(text);

        Assert.Equal("{\"a\":1,\"b\":2}", json);
    }

    [Fact]
    public void ExtractJsonFromText_NoJson_ReturnsNull()
    {
        Assert.Null(AnthropicAiQueryNode.ExtractJsonFromText("no json here at all"));
    }

    // --- ResolveMaxTokens (AB#4544) -------------------------------------------------
    // The AiConfiguration entity carries a maxTokens attribute; the node must prefer it
    // over its own MaxTokens property (which stays the fallback). Regression: the match
    // pipeline ran with the node value while the configuration said 8000 — long answers
    // were cut mid-JSON and the whole batch degraded.

    private static IMeshEtlContext EtlContextWithConfig(string configName, string? rawJson)
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined(configName)).Returns(rawJson != null);
        if (rawJson != null)
        {
            A.CallTo(() => globalConfiguration.GetRawJson(configName)).Returns(rawJson);
        }

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        return etlContext;
    }

    private static AnthropicAiQueryNodeConfiguration ConfigWith(string? configName, int nodeMaxTokens)
    {
        return new AnthropicAiQueryNodeConfiguration
        {
            ApiKeyConfigurationName = configName,
            MaxTokens = nodeMaxTokens,
            Question = "q",
        };
    }

    [Theory]
    [InlineData("{\"apiKey\": \"k\", \"maxTokens\": 8000}")] // JSON number
    [InlineData("{\"apiKey\": \"k\", \"maxTokens\": \"8000\"}")] // JSON string
    public void ResolveMaxTokens_ConfiguredOnEntity_WinsOverNodeProperty(string rawJson)
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", rawJson);

        var result = AnthropicAiQueryNode.ResolveMaxTokens(
            ConfigWith("AnthropicAiConfig", 4000), etlContext, A.Fake<INodeContext>());

        Assert.Equal(8000, result);
    }

    [Theory]
    [InlineData("{\"apiKey\": \"k\"}")] // attribute absent
    [InlineData("{\"apiKey\": \"k\", \"maxTokens\": 0}")] // not a usable value
    [InlineData("{\"apiKey\": \"k\", \"maxTokens\": -1}")]
    [InlineData("{\"apiKey\": \"k\", \"maxTokens\": \"lots\"}")] // not numeric
    public void ResolveMaxTokens_NoUsableEntityValue_FallsBackToNodeProperty(string rawJson)
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", rawJson);

        var result = AnthropicAiQueryNode.ResolveMaxTokens(
            ConfigWith("AnthropicAiConfig", 4000), etlContext, A.Fake<INodeContext>());

        Assert.Equal(4000, result);
    }

    [Fact]
    public void ResolveMaxTokens_NoConfigurationName_FallsBackToNodeProperty()
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", "{\"maxTokens\": 8000}");

        var result = AnthropicAiQueryNode.ResolveMaxTokens(
            ConfigWith(null, 4000), etlContext, A.Fake<INodeContext>());

        Assert.Equal(4000, result);
    }

    [Fact]
    public void ResolveMaxTokens_ConfigurationNotDefined_FallsBackToNodeProperty()
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", null);

        var result = AnthropicAiQueryNode.ResolveMaxTokens(
            ConfigWith("AnthropicAiConfig", 4000), etlContext, A.Fake<INodeContext>());

        Assert.Equal(4000, result);
    }

    // --- ResolveTemperature (AB#4544) -----------------------------------------------
    // Same dead-configuration defect as maxTokens: the AiConfiguration's temperature was
    // never read. 0.0 is a meaningful value (the accounting configuration uses it), so
    // only absence and out-of-range values fall back to the node property.

    [Theory]
    [InlineData("{\"apiKey\": \"k\", \"temperature\": 0.0}", 0.0)] // 0.0 is a real value, not "unset"
    [InlineData("{\"apiKey\": \"k\", \"temperature\": 0.7}", 0.7)]
    [InlineData("{\"apiKey\": \"k\", \"temperature\": \"0.7\"}", 0.7)] // JSON string
    [InlineData("{\"apiKey\": \"k\", \"temperature\": 1}", 1.0)] // integer literal
    public void ResolveTemperature_ConfiguredOnEntity_WinsOverNodeProperty(string rawJson, double expected)
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", rawJson);

        var result = AnthropicAiQueryNode.ResolveTemperature(
            ConfigWith("AnthropicAiConfig", 4000), etlContext, A.Fake<INodeContext>());

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("{\"apiKey\": \"k\"}")] // attribute absent
    [InlineData("{\"apiKey\": \"k\", \"temperature\": -0.1}")] // out of range
    [InlineData("{\"apiKey\": \"k\", \"temperature\": 1.5}")]
    [InlineData("{\"apiKey\": \"k\", \"temperature\": \"warm\"}")] // not numeric
    public void ResolveTemperature_NoUsableEntityValue_FallsBackToNodeProperty(string rawJson)
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", rawJson);
        var config = ConfigWith("AnthropicAiConfig", 4000);
        config.Temperature = 0.4;

        var result = AnthropicAiQueryNode.ResolveTemperature(config, etlContext, A.Fake<INodeContext>());

        Assert.Equal(0.4, result);
    }

    [Fact]
    public void ResolveTemperature_NoConfigurationName_FallsBackToNodeProperty()
    {
        var etlContext = EtlContextWithConfig("AnthropicAiConfig", "{\"temperature\": 0.7}");
        var config = ConfigWith(null, 4000);
        config.Temperature = 0.4;

        var result = AnthropicAiQueryNode.ResolveTemperature(config, etlContext, A.Fake<INodeContext>());

        Assert.Equal(0.4, result);
    }

    /// <summary>
    /// Pins <see cref="AnthropicAiQueryNode.BuildEffectiveSystemPrompt" /> (AB#4562 follow-up).
    /// When MCP is configured but zero tools were loaded, the tools-promising pipeline system
    /// prompt must be extended with the no-tools instruction — otherwise the model imitates
    /// tool calls as text and fabricates their results (observed with invented invoice data
    /// in the accounting chat). All other combinations must return the prompt unchanged.
    /// </summary>
    [Fact]
    public void BuildEffectiveSystemPrompt_McpConfiguredButNoTools_AppendsNoToolsInstruction()
    {
        const string prompt = "You have tools to query live data.";

        var result = AnthropicAiQueryNode.BuildEffectiveSystemPrompt(prompt, mcpConfigured: true, mcpToolCount: 0);

        Assert.StartsWith(prompt, result);
        Assert.EndsWith(AnthropicAiQueryNode.NoToolsSystemPromptSuffix, result);
    }

    [Fact]
    public void BuildEffectiveSystemPrompt_McpConfiguredWithTools_ReturnsPromptUnchanged()
    {
        const string prompt = "You have tools to query live data.";

        var result = AnthropicAiQueryNode.BuildEffectiveSystemPrompt(prompt, mcpConfigured: true, mcpToolCount: 3);

        Assert.Same(prompt, result);
    }

    [Fact]
    public void BuildEffectiveSystemPrompt_McpNotConfigured_ReturnsPromptUnchanged()
    {
        const string prompt = "You are a document extraction assistant.";

        var result = AnthropicAiQueryNode.BuildEffectiveSystemPrompt(prompt, mcpConfigured: false, mcpToolCount: 0);

        Assert.Same(prompt, result);
    }

    [Fact]
    public void BuildEffectiveSystemPrompt_EmptyPromptWithoutTools_ReturnsOnlyInstruction()
    {
        var result = AnthropicAiQueryNode.BuildEffectiveSystemPrompt(string.Empty, mcpConfigured: true, mcpToolCount: 0);

        Assert.Equal(AnthropicAiQueryNode.NoToolsSystemPromptSuffix.TrimStart(), result);
    }
}

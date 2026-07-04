using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
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
}

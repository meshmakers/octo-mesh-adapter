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
}

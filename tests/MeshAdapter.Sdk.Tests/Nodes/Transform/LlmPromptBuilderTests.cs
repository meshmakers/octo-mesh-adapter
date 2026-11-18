using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Microsoft.Extensions.AI;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The conversation history an LLM query reads from the data context. The prompt texts themselves
///     are covered by the tests of <c>AnthropicAiQuery@1</c>, which run the same code.
/// </summary>
public class LlmPromptBuilderTests
{
    private static List<ChatMessage>? Load(string dataJson, string? path = "$.history")
    {
        using var document = JsonDocument.Parse(dataJson);
        using var dataContext = new DataContextImpl(document.RootElement);
        return LlmPromptBuilder.LoadHistory(path, dataContext, A.Fake<INodeContext>());
    }

    [Fact]
    public void Entries_KeepTheirOrderRoleAndText()
    {
        var history = Load(
            """{"history":[{"role":"user","content":"Hallo"},{"role":"assistant","content":"Guten Tag"},{"role":"System","content":"as written"}]}""");

        Assert.NotNull(history);
        Assert.Equal(["user", "assistant", "System"], history.Select(m => m.Role.Value));
        Assert.Equal(["Hallo", "Guten Tag", "as written"], history.Select(m => m.Text));
    }

    [Fact]
    public void ContentParts_AreJoinedToOneText_OtherPartsAreLeftOut()
    {
        var history = Load(
            """{"history":[{"role":"user","content":[{"type":"text","text":"first"},{"type":"image_url","image_url":{"url":"x"}},{"type":"TEXT","text":"second"},{"type":"text","text":""},{"type":"text","text":5}]}]}""");

        var message = Assert.Single(history!);
        Assert.Equal("first" + Environment.NewLine + "second", message.Text);
    }

    [Theory]
    [InlineData("""{"history":[{"content":"no role"}]}""")]
    [InlineData("""{"history":[{"role":"","content":"empty role"}]}""")]
    [InlineData("""{"history":[{"role":5,"content":"role is a number"}]}""")]
    [InlineData("""{"history":[{"role":"user"}]}""")]
    [InlineData("""{"history":[{"role":"user","content":""}]}""")]
    [InlineData("""{"history":[{"role":"user","content":{"text":"an object"}}]}""")]
    [InlineData("""{"history":[{"role":"user","content":[{"type":"image_url"}]}]}""")]
    public void AnEntryWithoutRoleOrText_IsLeftOut(string dataJson)
    {
        Assert.Empty(Load(dataJson)!);
    }

    [Theory]
    [InlineData("""{"history":"not an array"}""", "$.history")]
    [InlineData("""{"other":[]}""", "$.history")]
    [InlineData("""{"history":[{"role":"user","content":"Hallo"}]}""", null)]
    [InlineData("""{"history":[{"role":"user","content":"Hallo"}]}""", "")]
    public void WithoutAnArrayAtThePath_ThereIsNoHistory(string dataJson, string? path)
    {
        Assert.Null(Load(dataJson, path));
    }
}

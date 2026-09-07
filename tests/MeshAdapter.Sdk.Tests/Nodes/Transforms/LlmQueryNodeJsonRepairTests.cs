using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Microsoft.Extensions.AI;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Behavior contracts of the JSON handling in LlmQuery@1 (AB#4861):
/// deterministic repair runs before any LLM call, and the LLM repair fallback is
/// cost-bounded by construction — no retry-until-timeout, no resending of the original
/// context. Assertions are on call counts and parse results, not message wording.
/// </summary>
public class LlmQueryNodeJsonRepairTests
{
    private const string ValidJson = """{"title":"ok","sections":[]}""";

    // Naked double quote inside a string value — the real-world failure class
    // (verbatim source quotes). Mechanically repairable.
    private const string NakedQuoteJson =
        """{"title":"Anleitung","quote":"Laufwerksbuchstaben "G:" muss angeschlossen werden"}""";

    // Unquoted token as value — NOT mechanically repairable; exercises the LLM tier.
    private const string UnrepairableJson = """{"title": oops}""";

    private static LlmQueryNodeConfiguration MakeConfig(int repairAttempts = 1) => new()
    {
        Question = "original question, must never be resent to the repair call",
        TargetPath = "$.out",
        ResponseFormat = "json",
        MaxJsonRepairAttempts = repairAttempts
    };

    private static ChatOptions MakeOptions() => new() { ModelId = "test-model", MaxOutputTokens = 128 };

    private static void AssertNoLlmCall(IChatClient client) =>
        A.CallTo(() => client.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions>._, A<CancellationToken>._))
            .MustNotHaveHappened();

    // ---- Deterministic tier ----

    [Fact]
    public async Task NakedQuoteInString_RepairedDeterministically_NoLlmCall()
    {
        var client = A.Fake<IChatClient>();

        var result = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
            NakedQuoteJson, MakeConfig(), client, MakeOptions(), A.Fake<INodeContext>(),
            CancellationToken.None);

        var element = Assert.IsType<JsonElement>(result);
        // Content preserved, quote spliced correctly — not truncated at the naked quote.
        Assert.Contains("G:", element.GetProperty("quote").GetString());
        Assert.EndsWith("angeschlossen werden", element.GetProperty("quote").GetString());
        AssertNoLlmCall(client);
    }

    [Fact]
    public void RepairMechanically_TrailingCommas_RemovedAndArrayLengthPreserved()
    {
        const string trailingCommas = """{"a":1,"b":[1,2,],}""";

        var repaired = LlmJsonResponseProcessor.RepairMechanically(trailingCommas);

        var element = JsonSerializer.Deserialize<JsonElement>(repaired);
        Assert.Equal(2, element.GetProperty("b").GetArrayLength());
    }

    [Fact]
    public void RepairMechanically_TruncatedOutput_ClosesStringAndContainers()
    {
        // Unterminated string plus unclosed array and object, as produced by a token cut-off.
        const string truncated = """{"a":[{"t":"cut off her""";

        var repaired = LlmJsonResponseProcessor.RepairMechanically(truncated);

        var element = JsonSerializer.Deserialize<JsonElement>(repaired);
        Assert.Equal("cut off her", element.GetProperty("a")[0].GetProperty("t").GetString());
    }

    [Fact]
    public void RepairMechanically_ValidJson_ReturnedUnchanged()
    {
        var repaired = LlmJsonResponseProcessor.RepairMechanically(ValidJson);

        Assert.Equal(ValidJson, repaired);
    }

    [Fact]
    public void SanitizeStringValues_ReplacesQuotesInValuesOnly_StructureIntact()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"attributes":{"content":"Laufwerk \"G:\" anschließen","count":3},"tags":["say \"hi\""]}""");

        var sanitized = LlmPromptBuilder.SanitizeStringValues(node);
        var json = sanitized!.ToJsonString();

        // Result is still valid JSON with intact structure...
        var round = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(3, round.GetProperty("attributes").GetProperty("count").GetInt32());
        // ...and content values carry no double-quote glyphs anymore (whole family -> ').
        Assert.Equal("Laufwerk 'G:' anschließen",
            round.GetProperty("attributes").GetProperty("content").GetString());
        Assert.Equal("say 'hi'", round.GetProperty("tags")[0].GetString());
    }

    // ---- LLM tier: cost contracts ----

    [Fact]
    public async Task ValidJson_NoRepairCall()
    {
        var client = A.Fake<IChatClient>();

        var result = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
            ValidJson, MakeConfig(), client, MakeOptions(), A.Fake<INodeContext>(),
            CancellationToken.None);

        Assert.IsType<JsonElement>(result);
        AssertNoLlmCall(client);
    }

    [Fact]
    public async Task LlmRepair_SendsBrokenOutputOnly_NeverTheOriginalContext()
    {
        var client = A.Fake<IChatClient>();
        IEnumerable<ChatMessage>? captured = null;
        A.CallTo(() => client.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> m, ChatOptions? _, CancellationToken _) =>
                captured = m.ToList())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidJson)));

        var result = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
            UnrepairableJson, MakeConfig(), client, MakeOptions(), A.Fake<INodeContext>(),
            CancellationToken.None);

        Assert.IsType<JsonElement>(result);
        A.CallTo(() => client.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // Cost contract: broken output goes back, the original prompt/context does not.
        var user = captured!.Single(m => m.Role == ChatRole.User);
        Assert.Contains("oops", user.Text);
        Assert.DoesNotContain("original question", user.Text);
    }

    [Fact]
    public async Task RepairDisabled_NoCall_ReturnsText()
    {
        var client = A.Fake<IChatClient>();

        var result = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
            UnrepairableJson, MakeConfig(repairAttempts: 0), client, MakeOptions(),
            A.Fake<INodeContext>(), CancellationToken.None);

        Assert.IsType<string>(result);
        AssertNoLlmCall(client);
    }

    [Fact]
    public async Task RepairKeepsFailing_HardCappedAtTwoCalls()
    {
        var client = A.Fake<IChatClient>();
        // Model keeps answering with unrepairable JSON; configured 99 must cap at 2.
        A.CallTo(() => client.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions>._, A<CancellationToken>._))
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, UnrepairableJson)));

        var result = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
            UnrepairableJson, MakeConfig(repairAttempts: 99), client, MakeOptions(),
            A.Fake<INodeContext>(), CancellationToken.None);

        Assert.IsType<string>(result);
        A.CallTo(() => client.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions>._, A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
    }
}

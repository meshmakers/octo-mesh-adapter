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

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     Guaranteed JSON of <c>AnthropicAiQuery@1</c> (AB#8498), ported from the KnowledgeCapture
///     prototype (<c>philipp/AB#4861_llmquery-guaranteed-json</c>). The cost contracts of the prototype
///     hold: the deterministic tiers run before any repair call, repair calls are hard-capped at two and
///     never resend the original context. New against the prototype: the answer is validated against the
///     schema, and an answer that stays invalid fails the node with the violated path instead of being
///     handed on as text.
/// </summary>
public class AnthropicAiQueryNodeJsonSchemaTests : NodeTestBase
{
    // The generation schema of the prototype's "generate" pipeline (KnowledgeCapture.MainLatest 1.3.0).
    // minItems is not supported by constrained decoding, so it is enforced client-side only.
    private const string WikiSchema =
        """{"type":"object","properties":{"title":{"type":"string"},"sections":{"type":"array","minItems":2,"items":{"type":"object","properties":{"heading":{"type":"string"},"sectionContent":{"type":"string"},"sortOrder":{"type":"integer"}},"required":["heading","sectionContent","sortOrder"],"additionalProperties":false}}},"required":["title","sections"],"additionalProperties":false}""";

    private const string ValidWiki =
        """{"title":"Netzlaufwerk","sections":[{"heading":"Zweck","sectionContent":"a","sortOrder":1},{"heading":"Schritte","sectionContent":"b","sortOrder":2}]}""";

    // Real-world failure class from the prototype: a verbatim source quote with naked double quotes.
    private const string NakedQuoteWiki =
        """{"title":"Netzlaufwerk","sections":[{"heading":"Schritte","sectionContent":"Laufwerksbuchstaben "G:" muss angeschlossen werden","sortOrder":1},{"heading":"Hinweis","sectionContent":"ok","sortOrder":2}]}""";

    // Parses, but the second section misses sortOrder.
    private const string MissingSortOrderWiki =
        """{"title":"Netzlaufwerk","sections":[{"heading":"Zweck","sectionContent":"a","sortOrder":1},{"heading":"Schritte","sectionContent":"b"}]}""";

    // Unquoted token as value — not mechanically repairable.
    private const string UnrepairableJson = """{"title": oops}""";

    private static readonly NJsonSchema.JsonSchema Schema = AiJsonSchemaResponseProcessor.ParseSchema(WikiSchema);

    private static Task<object> Process(string answer, int attempts, Func<string, Task<string>> repair) =>
        AiJsonSchemaResponseProcessor.ProcessAsync(answer, WikiSchema, Schema, attempts, repair,
            A.Fake<INodeContext>());

    private static Func<string, Task<string>> NoRepairExpected() =>
        _ => throw new InvalidOperationException("No repair call expected");

    // ---- Deterministic tiers: no repair call ----

    [Fact]
    public async Task ValidAnswer_NoRepairCall()
    {
        var result = await Process(ValidWiki, 1, NoRepairExpected());

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(2, element.GetProperty("sections").GetArrayLength());
    }

    [Fact]
    public async Task NakedQuoteInString_RepairedDeterministically_NoRepairCall()
    {
        var result = await Process(NakedQuoteWiki, 1, NoRepairExpected());

        var element = Assert.IsType<JsonElement>(result);
        var content = element.GetProperty("sections")[0].GetProperty("sectionContent").GetString();
        Assert.Contains("G:", content);
        Assert.EndsWith("angeschlossen werden", content);
    }

    [Fact]
    public async Task ProseAndFenceAroundTheAnswer_ExtractedDeterministically_NoRepairCall()
    {
        var result = await Process($"Here you go: ```json {ValidWiki} ``` done", 1, NoRepairExpected());

        Assert.IsType<JsonElement>(result);
    }

    [Fact]
    public async Task ProseBracketsBeforeTheObject_TheValidCandidateIsUsed_NoRepairCall()
    {
        var result = await Process($"Result [draft]: {ValidWiki} — done.", 1, NoRepairExpected());

        Assert.IsType<JsonElement>(result);
    }

    [Fact]
    public async Task RepeatedKey_LastValueWinsBeforeValidation_NoRepairCall()
    {
        // AB#5348 stays in force in schema mode: the duplicate title is normalised, then validated.
        const string duplicate =
            """{"title":"first","title":"second","sections":[{"heading":"a","sectionContent":"a","sortOrder":1},{"heading":"b","sectionContent":"b","sortOrder":2}]}""";

        var result = await Process(duplicate, 1, NoRepairExpected());

        var node = Assert.IsAssignableFrom<JsonNode>(result);
        Assert.Equal("second", node["title"]!.GetValue<string>());
    }

    [Fact]
    public void RepairMechanically_TrailingCommas_RemovedAndArrayLengthPreserved()
    {
        var repaired = LlmJsonExtractor.RepairMechanically("""{"a":1,"b":[1,2,],}""");

        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(repaired).GetProperty("b").GetArrayLength());
    }

    [Fact]
    public void RepairMechanically_TruncatedOutput_ClosesStringAndContainers()
    {
        var repaired = LlmJsonExtractor.RepairMechanically("""{"a":[{"t":"cut off her""");

        Assert.Equal("cut off her",
            JsonSerializer.Deserialize<JsonElement>(repaired).GetProperty("a")[0].GetProperty("t").GetString());
    }

    [Fact]
    public void RepairMechanically_ValidJson_ReturnedUnchanged()
    {
        Assert.Equal(ValidWiki, LlmJsonExtractor.RepairMechanically(ValidWiki));
    }

    [Fact]
    public void ExtractJsonFromText_NoCandidateParses_ReturnsTheFirstForMechanicalRepair()
    {
        Assert.Equal("""{"a":1,}""", LlmJsonExtractor.ExtractJsonFromText("""Result: {"a":1,} then {"b":2,}"""));
    }

    // ---- Repair calls: cost contracts ----

    [Fact]
    public async Task RepairCall_SendsBrokenAnswerErrorsAndSchema_Only()
    {
        var prompts = new List<string>();

        var result = await Process(MissingSortOrderWiki, 1, prompt =>
        {
            prompts.Add(prompt);
            return Task.FromResult(ValidWiki);
        });

        Assert.IsType<JsonElement>(result);
        var prompt = Assert.Single(prompts);
        Assert.Contains(MissingSortOrderWiki, prompt);
        Assert.Contains("$.sections[1]", prompt);
        Assert.Contains("sortOrder", prompt);
        Assert.Contains(WikiSchema, prompt);
    }

    [Fact]
    public async Task UnparseableAnswer_RepairedByOneCall()
    {
        var calls = 0;

        var result = await Process(UnrepairableJson, 1, _ =>
        {
            calls++;
            return Task.FromResult(ValidWiki);
        });

        Assert.IsType<JsonElement>(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RepairDisabled_NoCall_FailsNamingThePath()
    {
        var ex = await Assert.ThrowsAsync<JsonSchemaViolationException>(
            () => Process(MissingSortOrderWiki, 0, NoRepairExpected()));

        Assert.Contains(ex.Errors, e => e.StartsWith("$.sections[1]", StringComparison.Ordinal));
        Assert.Contains("$.sections[1].sortOrder: PropertyRequired", ex.Message);
    }

    [Fact]
    public async Task RepairKeepsFailing_HardCappedAtTwoCalls_ThenFails()
    {
        var calls = 0;

        var ex = await Assert.ThrowsAsync<JsonSchemaViolationException>(() => Process(UnrepairableJson, 99, _ =>
        {
            calls++;
            return Task.FromResult(UnrepairableJson);
        }));

        Assert.Equal(2, calls);
        Assert.Contains("$: not valid JSON", ex.Message);
    }

    [Fact]
    public async Task ClientSideOnlyConstraint_MinItems_IsEnforced()
    {
        // minItems is stripped from the API schema, so only the client validation catches it.
        const string oneSection = """{"title":"t","sections":[{"heading":"a","sectionContent":"a","sortOrder":1}]}""";

        var ex = await Assert.ThrowsAsync<JsonSchemaViolationException>(
            () => Process(oneSection, 0, NoRepairExpected()));

        Assert.Contains(ex.Errors, e => e.StartsWith("$.sections", StringComparison.Ordinal) &&
                                        e.Contains("TooFewItems", StringComparison.Ordinal));
    }

    // ---- Schema handling ----

    [Fact]
    public void ParseSchema_NotJson_FailsBeforeAnyCall()
    {
        var ex = Assert.Throws<ArgumentException>(() => AiJsonSchemaResponseProcessor.ParseSchema("{not json"));

        Assert.Contains("jsonSchema", ex.Message);
    }

    [Fact]
    public void BuildApiSchema_StripsUnsupportedKeywords_AddsAdditionalPropertiesFalse_KeepsPropertyNames()
    {
        const string schema =
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"pattern":{"type":"string","maxLength":5},"items":{"type":"array","minItems":1,"items":{"type":"object","properties":{"n":{"type":"integer","minimum":0}}}}}}""";

        var api = AiJsonSchemaResponseProcessor.BuildApiSchema(schema).AsObject();

        Assert.False(api.ContainsKey("$schema"));
        Assert.False(api["additionalProperties"]!.GetValue<bool>());
        var properties = api["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("pattern")); // a property NAME, not the keyword
        Assert.False(properties["pattern"]!.AsObject().ContainsKey("maxLength"));
        Assert.False(properties["items"]!.AsObject().ContainsKey("minItems"));
        var item = properties["items"]!["items"]!.AsObject();
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.False(item["properties"]!["n"]!.AsObject().ContainsKey("minimum"));
    }

    [Theory]
    [InlineData("#/sections[1].sortOrder", "$.sections[1].sortOrder")]
    [InlineData("#/title", "$.title")]
    [InlineData("#", "$")]
    [InlineData(null, "$")]
    public void ToJsonPath_TranslatesSchemaPaths(string? schemaPath, string expected)
    {
        Assert.Equal(expected, AiJsonSchemaResponseProcessor.ToJsonPath(schemaPath));
    }

    [Fact]
    public void BuildRequestBody_WithSchema_SendsOutputConfigFormat()
    {
        var apiSchema = AiJsonSchemaResponseProcessor.BuildApiSchema(WikiSchema);

        var body = AnthropicAiQueryNode.BuildRequestBody("m", 100, 0.1, "sys", [], null, apiSchema);

        var json = JsonNode.Parse(JsonSerializer.Serialize(body))!;
        Assert.Equal("json_schema", json["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal("object", json["output_config"]!["format"]!["schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void BuildRequestBody_WithoutSchema_SendsNoOutputConfig()
    {
        var body = AnthropicAiQueryNode.BuildRequestBody("m", 100, 0.1, "sys", [], null);

        Assert.False(body.ContainsKey("output_config"));
    }

    [Fact]
    public void BuildUserPrompt_WithSchema_StatesTheSchemaInsteadOfTheInvoiceSample()
    {
        var prompt = AnthropicAiQueryNode.BuildUserPrompt("q", "ctx", "json",
            new AnthropicAiQueryNodeConfiguration { Question = "q" }.JsonFormatSample, WikiSchema);

        Assert.Contains(WikiSchema, prompt);
        Assert.DoesNotContain("grossTotal", prompt);
    }

    // ---- Whole node against a faked Messages API ----

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private int _index;
        public List<JsonNode> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            var body = responseBodies[Math.Min(_index++, responseBodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string TextResponse(string text) =>
        new JsonObject
        {
            ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        }.ToJsonString();

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, RecordingHandler Handler)
        PrepareNode(AnthropicAiQueryNodeConfiguration config, params string[] answers)
    {
        var handler = new RecordingHandler(answers.Select(TextResponse).ToArray());
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler, false));

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(null);

        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("ORIGINAL SOURCE DOCUMENT TEXT");

        var node = new AnthropicAiQueryNode(next, etlContext, factory, A.Fake<IServiceAccountTokenService>());
        return (node, dataContext, nodeContext, handler);
    }

    private static AnthropicAiQueryNodeConfiguration Config(string? schema, int repairs = 1) => new()
    {
        ApiKey = "test-key",
        Model = "claude-test",
        Question = "ORIGINAL QUESTION",
        Path = "$.text",
        TargetPath = "$.out",
        JsonSchema = schema,
        MaxJsonRepairAttempts = repairs
    };

    [Fact]
    public async Task Node_WithSchema_InvalidAnswer_IsRepairedByOneContextFreeCall_AndWritten()
    {
        var (node, dataContext, nodeContext, handler) =
            PrepareNode(Config(WikiSchema), MissingSortOrderWiki, ValidWiki);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, handler.RequestBodies.Count);
        var first = handler.RequestBodies[0];
        Assert.Equal("json_schema", first["output_config"]!["format"]!["type"]!.GetValue<string>());

        var repair = handler.RequestBodies[1];
        var repairText = repair.ToJsonString();
        Assert.DoesNotContain("ORIGINAL QUESTION", repairText);
        Assert.DoesNotContain("ORIGINAL SOURCE DOCUMENT TEXT", repairText);
        Assert.Null(repair["tools"]);
        Assert.Equal(0.0, repair["temperature"]!.GetValue<double>());
        Assert.NotNull(repair["output_config"]);

        A.CallTo(() => dataContext.Set("$.out",
                A<object?>.That.Matches(v => v is JsonElement && ((JsonElement)v!).GetProperty("sections")
                    .GetArrayLength() == 2),
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Node_WithSchema_StillInvalidAfterRepairs_FailsNamingThePath()
    {
        var (node, dataContext, nodeContext, handler) =
            PrepareNode(Config(WikiSchema), MissingSortOrderWiki, MissingSortOrderWiki);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("$.sections[1]", ex.ToString());
        Assert.Equal(2, handler.RequestBodies.Count); // original + one repair (default 1)
        A.CallTo(() => dataContext.Set(A<string>._, A<object?>._, A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Node_WithoutSchema_BehaviourUnchanged_NoOutputConfig_NoRepair_TextFallback()
    {
        var (node, dataContext, nodeContext, handler) = PrepareNode(Config(null), UnrepairableJson);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Null(request["output_config"]);
        A.CallTo(() => dataContext.Set<object?>("$.out", UnrepairableJson, A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Node_SchemaOnTextFormat_IsIgnored()
    {
        var config = Config(WikiSchema);
        config.ResponseFormat = "text";
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, "plain answer");

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Null(Assert.Single(handler.RequestBodies)["output_config"]);
    }
}

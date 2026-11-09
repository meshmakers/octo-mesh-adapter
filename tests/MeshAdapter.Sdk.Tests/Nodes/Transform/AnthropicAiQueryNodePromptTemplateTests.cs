using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     Prompt by name for <c>AnthropicAiQuery@1</c> (AB#8498, decisions Odin 2026-11-09): the node loads
///     system prompt, question and model settings from a <c>System.Ai/AiPromptTemplate</c> with
///     <c>PromptPurpose</c> KnowledgeGeneration that the pipeline's configuration hand-over carries;
///     inline prompt texts override the template; model settings go template → AiConfiguration → node;
///     a missing template fails naming it; a pipeline without template behaves exactly as before.
/// </summary>
public class AnthropicAiQueryNodePromptTemplateTests : NodeTestBase
{
    private const string TemplateName = "knowledge-ingest";
    private const string AiConfigName = "AnthropicAiConfig";

    // The serialized runtime entity as the controller hands it over: CK attributes in a nested
    // "attributes" object with PascalCase keys, the enum as its key.
    private static string TemplateJson(int? purpose = 1, string? userTemplate = "TEMPLATE QUESTION",
        string? model = "claude-template-model", double? temperature = 0.3, int? maxTokens = 4000,
        string? fewShot = "<example>EXAMPLE</example>")
    {
        var attributes = new JsonObject
        {
            ["Name"] = TemplateName,
            ["SystemPromptFragment"] = "TEMPLATE SYSTEM PROMPT",
            ["TemplateVersion"] = 3
        };
        if (purpose.HasValue) attributes["PromptPurpose"] = purpose.Value;
        if (userTemplate != null) attributes["UserTemplate"] = userTemplate;
        if (model != null) attributes["DefaultModelId"] = model;
        if (temperature.HasValue) attributes["Temperature"] = temperature.Value;
        if (maxTokens.HasValue) attributes["MaxTokens"] = maxTokens.Value;
        if (fewShot != null) attributes["FewShotExamples"] = fewShot;

        return new JsonObject
        {
            ["rtId"] = "650000000000000000000001",
            ["rtWellKnownName"] = TemplateName,
            ["attributes"] = attributes
        }.ToJsonString();
    }

    private static string AiConfigJson(string? aiModel = "claude-config-model", double? temperature = 0.7,
        int? maxTokens = 2000)
    {
        var json = new JsonObject { ["apiKey"] = "config-key" };
        if (aiModel != null) json["aiModel"] = aiModel;
        if (temperature.HasValue) json["temperature"] = temperature.Value;
        if (maxTokens.HasValue) json["maxTokens"] = maxTokens.Value;
        return json.ToJsonString();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonNode> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            var body = new JsonObject
            {
                ["stop_reason"] = "end_turn",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = """{"ok":true}""" })
            }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private (AnthropicAiQueryNode Node, IDataContext DataContext, INodeContext NodeContext, RecordingHandler Handler)
        PrepareNode(AnthropicAiQueryNodeConfiguration config, Dictionary<string, (string Json, string? Type)> entries)
    {
        var handler = new RecordingHandler();
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler, false));

        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.IsDefined(A<string>._))
            .ReturnsLazily((string name) => entries.ContainsKey(name));
        A.CallTo(() => globalConfiguration.GetRawJson(A<string>._))
            .ReturnsLazily((string name) => entries[name].Json);
        A.CallTo(() => globalConfiguration.GetConfigurationTypeId(A<string>._))
            .ReturnsLazily((string name) => entries.TryGetValue(name, out var e) && e.Type != null
                ? new RtCkId<CkTypeId>(e.Type)
                : null);

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns("testTenant");
        A.CallTo(() => etlContext.VerifiedPrincipal).Returns(null);
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);

        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetKind("$.text")).Returns(DataKind.String);
        A.CallTo(() => dataContext.Get<string>("$.text")).Returns("SOURCE TEXT");

        var node = new AnthropicAiQueryNode(next, etlContext, factory, A.Fake<IServiceAccountTokenService>(),
            A.Fake<IServiceClientAccessToken>());
        return (node, dataContext, nodeContext, handler);
    }

    private static Dictionary<string, (string Json, string? Type)> Template(string json) =>
        new() { [TemplateName] = (json, AiPromptTemplateResolver.AiPromptTemplateTypeFullName) };

    private static AnthropicAiQueryNodeConfiguration TemplateConfig() => new()
    {
        ApiKey = "test-key",
        PromptTemplateName = TemplateName,
        Path = "$.text",
        TargetPath = "$.out"
    };

    private static string SystemText(JsonNode request) => request["system"]![0]!["text"]!.GetValue<string>();

    private static string UserText(JsonNode request) =>
        request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();

    // ---- AC2: load by name ----

    [Fact]
    public async Task Template_SuppliesSystemPrompt_Question_AndModelSettings()
    {
        var (node, dataContext, nodeContext, handler) = PrepareNode(TemplateConfig(), Template(TemplateJson()));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var request = Assert.Single(handler.RequestBodies);
        var system = SystemText(request);
        Assert.StartsWith("TEMPLATE SYSTEM PROMPT", system);
        Assert.Contains("<examples>\n<example>EXAMPLE</example>\n</examples>", system);
        Assert.Contains("TEMPLATE QUESTION", UserText(request));
        Assert.Contains("SOURCE TEXT", UserText(request));
        Assert.Equal("claude-template-model", request["model"]!.GetValue<string>());
        Assert.Equal(0.3, request["temperature"]!.GetValue<double>());
        Assert.Equal(4000, request["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task InlinePromptTexts_OverrideTheTemplate_ModelSettingsStayFromTheTemplate()
    {
        var config = TemplateConfig();
        config.Question = "INLINE QUESTION";
        config.SystemPrompt = "INLINE SYSTEM PROMPT";
        config.Model = "claude-node-model";
        config.Temperature = 0.9;
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, Template(TemplateJson()));

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Equal("INLINE SYSTEM PROMPT", SystemText(request));
        Assert.Contains("INLINE QUESTION", UserText(request));
        Assert.DoesNotContain("TEMPLATE QUESTION", UserText(request));
        Assert.Equal("claude-template-model", request["model"]!.GetValue<string>());
        Assert.Equal(0.3, request["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Precedence_TemplateBeatsAiConfiguration_BeatsNode()
    {
        var config = TemplateConfig();
        config.ApiKey = null;
        config.ApiKeyConfigurationName = AiConfigName;
        config.Model = "claude-node-model";
        config.MaxTokens = 111;
        // The template sets only the model; temperature and max tokens fall through.
        var entries = Template(TemplateJson(temperature: null, maxTokens: null));
        entries[AiConfigName] = (AiConfigJson(maxTokens: null), "System.Communication/AiConfiguration");
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, entries);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Equal("claude-template-model", request["model"]!.GetValue<string>()); // template
        Assert.Equal(0.7, request["temperature"]!.GetValue<double>()); // AiConfiguration
        Assert.Equal(111, request["max_tokens"]!.GetValue<int>()); // node
    }

    [Fact]
    public async Task TemplateWithoutModel_FallsBackToAiConfiguration()
    {
        var config = TemplateConfig();
        config.ApiKey = null;
        config.ApiKeyConfigurationName = AiConfigName;
        var entries = Template(TemplateJson(model: null));
        entries[AiConfigName] = (AiConfigJson(), "System.Communication/AiConfiguration");
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, entries);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("claude-config-model", Assert.Single(handler.RequestBodies)["model"]!.GetValue<string>());
    }

    // ---- AC2: clear errors, before any paid call ----

    [Fact]
    public async Task MissingTemplate_FailsNamingTheTemplate_WithoutAnyApiCall()
    {
        var (node, dataContext, nodeContext, handler) = PrepareNode(TemplateConfig(), []);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains($"AiPromptTemplate '{TemplateName}' not found", ex.ToString());
        Assert.Contains("'Uses' association", ex.ToString());
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task TemplateOfAnotherPurpose_Fails(int? purpose)
    {
        var (node, dataContext, nodeContext, handler) =
            PrepareNode(TemplateConfig(), Template(TemplateJson(purpose: purpose)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains($"AiPromptTemplate '{TemplateName}' does not have PromptPurpose KnowledgeGeneration",
            ex.ToString());
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task ConfigurationOfAnotherCkType_Fails()
    {
        var entries = new Dictionary<string, (string Json, string? Type)>
        {
            [TemplateName] = (AiConfigJson(), "System.Communication/AiConfiguration")
        };
        var (node, dataContext, nodeContext, handler) = PrepareNode(TemplateConfig(), entries);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("not an System.Ai/AiPromptTemplate", ex.ToString());
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task TemplateWithoutUserTemplate_AndNoQuestion_FailsNamingTheTemplate()
    {
        var (node, dataContext, nodeContext, handler) =
            PrepareNode(TemplateConfig(), Template(TemplateJson(userTemplate: null)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains($"AiPromptTemplate '{TemplateName}' has no UserTemplate", ex.ToString());
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public void Resolver_AcceptsThePurposeByName()
    {
        var gc = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => gc.IsDefined(TemplateName)).Returns(true);
        A.CallTo(() => gc.GetConfigurationTypeId(TemplateName)).Returns(null);
        A.CallTo(() => gc.GetRawJson(TemplateName)).Returns(TemplateJson().Replace("\"PromptPurpose\":1",
            "\"PromptPurpose\":\"KnowledgeGeneration\"", StringComparison.Ordinal));

        var template = AiPromptTemplateResolver.Resolve(gc, TemplateName);

        Assert.NotNull(template);
        Assert.Equal(3, template.TemplateVersion);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void Resolver_IgnoresATemperatureOutOfRange(double temperature)
    {
        var gc = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => gc.IsDefined(TemplateName)).Returns(true);
        A.CallTo(() => gc.GetConfigurationTypeId(TemplateName)).Returns(null);
        A.CallTo(() => gc.GetRawJson(TemplateName)).Returns(TemplateJson(temperature: temperature));

        Assert.Null(AiPromptTemplateResolver.Resolve(gc, TemplateName)!.Temperature);
    }

    // ---- AC4: existing pipelines unchanged ----

    /// <summary>A real pipeline block (MeshmakersAccounting document-analysis, shortened question).</summary>
    private const string ExistingPipelineBlock = """
          - type: AnthropicAiQuery@1
            description: Extract document metadata + detect direction
            apiKeyConfigurationName: AnthropicAiConfig
            path: $.ocr.result
            dataPaths:
              - $.company
            responseFormat: json
            temperature: 0.1
            question: >-
              Extract accounting document information from the text in "Main Content".
            targetPath: $.ai.result
            continueOnError: true
        """;

    private static async Task<AnthropicAiQueryNodeConfiguration> DeserializeAsync(string yaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(AnthropicAiQueryNode));
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + yaml);
        return root.Transformations!.OfType<AnthropicAiQueryNodeConfiguration>().Single();
    }

    [Fact]
    public async Task ExistingPipelineBlock_Deserializes_WithoutTemplate_AndKeepsTheOldDefaults()
    {
        var config = await DeserializeAsync(ExistingPipelineBlock);

        Assert.Null(config.PromptTemplateName);
        Assert.Null(config.SystemPrompt);
        Assert.StartsWith("Extract accounting document information", config.Question);
        Assert.Equal(AnthropicAiQueryNodeConfiguration.DefaultSystemPrompt,
            AnthropicAiQueryNode.ResolveSystemPrompt(config, null));
        Assert.Equal(config.Question, AnthropicAiQueryNode.ResolveQuestion(config, null));
    }

    [Fact]
    public async Task PromptTemplateName_DeserializesAndQuestionIsOptional()
    {
        var config = await DeserializeAsync("""
              - type: AnthropicAiQuery@1
                apiKeyConfigurationName: AnthropicAiConfig
                promptTemplateName: knowledge-ingest
                path: $.text
                targetPath: $.out
            """);

        Assert.Equal(TemplateName, config.PromptTemplateName);
        Assert.Null(config.Question);
    }

    [Fact]
    public async Task NodeWithoutTemplate_SendsTheOldDefaultSystemPrompt_AndTheNodeSettings()
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKey = "test-key",
            Model = "claude-node-model",
            Question = "NODE QUESTION",
            Path = "$.text",
            TargetPath = "$.out"
        };
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, []);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var request = Assert.Single(handler.RequestBodies);
        Assert.Equal(AnthropicAiQueryNodeConfiguration.DefaultSystemPrompt, SystemText(request));
        Assert.Contains("NODE QUESTION", UserText(request));
        Assert.Equal("claude-node-model", request["model"]!.GetValue<string>());
        Assert.Equal(0.1, request["temperature"]!.GetValue<double>());
        Assert.Equal(1000, request["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void ExplicitEmptySystemPrompt_StillSendsNone()
    {
        // Before AB#8498 "systemPrompt: ''" sent no system prompt; an empty inline value is still "set".
        var config = new AnthropicAiQueryNodeConfiguration { Question = "q", SystemPrompt = "" };

        Assert.Equal("", AnthropicAiQueryNode.ResolveSystemPrompt(config, null));
    }

    [Fact]
    public async Task NodeWithoutQuestionAndTemplate_FailsAsBefore()
    {
        var config = new AnthropicAiQueryNodeConfiguration
        {
            ApiKey = "test-key", Model = "m", Path = "$.text", TargetPath = "$.out"
        };
        var (node, dataContext, nodeContext, handler) = PrepareNode(config, []);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("Question is required", ex.ToString());
        Assert.Empty(handler.RequestBodies);
    }
}

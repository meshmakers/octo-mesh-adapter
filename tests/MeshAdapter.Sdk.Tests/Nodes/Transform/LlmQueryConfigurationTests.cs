using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The configuration of <c>LlmQuery@1</c>: its defaults, the rules its setters enforce when the pipeline
///     definition is read, and its shape in the pipeline JSON schema.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class LlmQueryConfigurationTests(PipelineSchemaFixture schema)
{
    [Fact]
    public void Defaults()
    {
        var config = new LlmQueryNodeConfiguration();

        Assert.Equal(LlmProvider.Anthropic, config.Provider);
        Assert.Null(config.BaseUrl);
        Assert.Null(config.Temperature);
        Assert.Equal(4096, config.MaxTokens);
        Assert.Equal(string.Empty, config.JsonFormatSample);
        Assert.Equal("json", config.ResponseFormat);
        Assert.Equal(10, config.MaxToolRounds);
        Assert.Equal(50_000, config.MaxToolResultChars);
        Assert.Equal(1, config.MaxJsonRepairAttempts);
        Assert.Equal(NodeExecutionIdentity.ServiceAccount, config.Identity);
        Assert.Null(config.Question);
        Assert.Null(config.SystemPrompt);
    }

    [Theory]
    // lifted from the prototype's rule: clear text only on loopback
    [InlineData("http://llm.example.com/v1", false)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://llm.example.com/v1", true)]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic", true)]
    [InlineData("https://my-resource.services.ai.azure.com/anthropic/", true)]
    // added: what the key must not travel to or with
    [InlineData("/v1/messages", false)]
    [InlineData("api.anthropic.com", false)]
    [InlineData("ftp://llm.example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://user:secret@llm.example.com", false)]
    [InlineData("https://llm.example.com/v1?key=secret", false)]
    [InlineData("https://llm.example.com/v1#frag", false)]
    [InlineData("https://llm.example.com/v1?", false)]
    public void BaseUrl_IsCheckedWhenSet(string baseUrl, bool accepted)
    {
        var config = new LlmQueryNodeConfiguration();

        if (accepted)
        {
            config.BaseUrl = baseUrl;
            Assert.Equal(baseUrl, config.BaseUrl);
            return;
        }

        var ex = Assert.Throws<ArgumentException>(() => config.BaseUrl = baseUrl);
        Assert.Equal("baseUrl", ex.ParamName);
        Assert.Null(config.BaseUrl);
    }

    [Theory]
    [InlineData("https://user:secret@llm.example.com")]
    [InlineData("https://llm.example.com/v1?key=secret")]
    [InlineData("http://llm.example.com/v1#secret")]
    public void ARefusedBaseUrl_ThatMayHoldACredential_IsNotRepeatedInTheMessage(string baseUrl)
    {
        var ex = Assert.Throws<ArgumentException>(() => new LlmQueryNodeConfiguration { BaseUrl = baseUrl });

        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyBaseUrl_IsTheDefaultEndpoint(string? baseUrl)
    {
        Assert.Null(LlmBaseUrl.Parse(baseUrl));
        Assert.Equal(baseUrl, new LlmQueryNodeConfiguration { BaseUrl = baseUrl }.BaseUrl);
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("text", true)]
    [InlineData("JSON", true)]
    [InlineData("Text", true)]
    [InlineData("jsno", false)]
    [InlineData("", false)]
    [InlineData("xml", false)]
    public void ResponseFormat_IsJsonOrText(string value, bool accepted)
    {
        var config = new LlmQueryNodeConfiguration();

        if (accepted)
        {
            config.ResponseFormat = value;
            Assert.Equal(value, config.ResponseFormat);
        }
        else
        {
            Assert.Equal("responseFormat", Assert.Throws<ArgumentException>(() => config.ResponseFormat = value).ParamName);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(50_000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void MaxToolResultChars_IsAtLeastOne(int value, bool accepted)
    {
        var config = new LlmQueryNodeConfiguration();

        if (accepted)
        {
            config.MaxToolResultChars = value;
            Assert.Equal(value, config.MaxToolResultChars);
        }
        else
        {
            Assert.Equal("maxToolResultChars",
                Assert.Throws<ArgumentException>(() => config.MaxToolResultChars = value).ParamName);
        }
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.7, true)]
    [InlineData(1.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(1.5, false)]
    [InlineData(double.NaN, false)]
    public void Temperature_IsBetweenZeroAndOne(double value, bool accepted)
    {
        var config = new LlmQueryNodeConfiguration();

        if (accepted)
        {
            config.Temperature = value;
            Assert.Equal(value, config.Temperature);
        }
        else
        {
            Assert.Equal("temperature",
                Assert.Throws<ArgumentException>(() => config.Temperature = value).ParamName);
            Assert.Null(config.Temperature);
        }
    }

    [Fact]
    public void AnUndefinedProvider_IsRefused()
    {
        Assert.Equal("provider",
            Assert.Throws<ArgumentException>(() => new LlmQueryNodeConfiguration { Provider = (LlmProvider)7 })
                .ParamName);
    }

    [Fact]
    public async Task AFullPipeline_IsReadWithEveryValueAsWritten()
    {
        var root = await Serializer().DeserializeAsync("""
            transformations:
              - type: LlmQuery@1
                provider: Anthropic
                baseUrl: https://my-resource.services.ai.azure.com/anthropic
                apiKeyConfigurationName: foundry-claude
                model: claude-haiku-5-5
                promptTemplateName: knowledge-ingest
                question: Summarise.
                systemPrompt: Be brief.
                path: $.document.text
                dataPaths: [$.document.meta]
                conversationHistoryPath: $.input.history
                maxTokens: 2048
                temperature: 0.2
                responseFormat: text
                maxJsonRepairAttempts: 2
                usageOutputPath: $.usage
                cacheSource: true
                mcpServerUrl: https://mcp.example
                mcpServiceAccountConfigName: McpServiceAccount
                identity: Caller
                mcpToolNames: [query_entities]
                maxToolRounds: 4
                maxToolResultChars: 1000
                continueOnError: true
                targetPath: $.ai.result
            """);

        var config = Assert.IsType<LlmQueryNodeConfiguration>(Assert.Single(root.Transformations!));
        Assert.Equal(LlmProvider.Anthropic, config.Provider);
        Assert.Equal("https://my-resource.services.ai.azure.com/anthropic", config.BaseUrl);
        Assert.Equal("foundry-claude", config.ApiKeyConfigurationName);
        Assert.Equal("claude-haiku-5-5", config.Model);
        Assert.Equal(0.2, config.Temperature);
        Assert.Equal("text", config.ResponseFormat);
        Assert.Equal(NodeExecutionIdentity.Caller, config.Identity);
        Assert.Equal(1000, config.MaxToolResultChars);
        Assert.Equal(4, config.MaxToolRounds);
        Assert.Equal(["query_entities"], config.McpToolNames!);
    }

    [Fact]
    public async Task ExplicitNulls_AreTheDefaults()
    {
        var root = await Serializer().DeserializeAsync("""
            transformations:
              - type: LlmQuery@1
                baseUrl: ~
                temperature: ~
                responseFormat: ~
                maxToolResultChars: ~
            """);

        var config = Assert.IsType<LlmQueryNodeConfiguration>(Assert.Single(root.Transformations!));
        Assert.Null(config.BaseUrl);
        Assert.Null(config.Temperature);
        Assert.Equal("json", config.ResponseFormat);
        Assert.Equal(50_000, config.MaxToolResultChars);
    }

    [Theory]
    [InlineData("baseUrl: http://llm.example.com", "baseUrl")]
    [InlineData("responseFormat: jsno", "responseFormat")]
    [InlineData("maxToolResultChars: 0", "maxToolResultChars")]
    [InlineData("temperature: 1.5", "temperature")]
    [InlineData("provider: OpenAiCompatible", "OpenAiCompatible")]
    public async Task ARefusedValue_FailsWhenThePipelineIsRead_NamingIt(string line, string named)
    {
        var yaml = $"""
                    transformations:
                      - type: LlmQuery@1
                        {line}
                    """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Serializer().DeserializeAsync(yaml));

        Assert.Contains(named, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("apiKey: sk-inline")]
    [InlineData("mcpDelegateToCaller: true")]
    [InlineData("topP: 0.9")]
    [InlineData("mcpConfigurationNames: [a]")]
    public async Task OptionsTheNodeDoesNotHave_AreRefused(string line)
    {
        var yaml = $"""
                    transformations:
                      - type: LlmQuery@1
                        {line}
                    """;

        await Assert.ThrowsAsync<PipelineSerializationException>(() => Serializer().DeserializeAsync(yaml));
    }

    [Fact]
    public void Schema_HasProviderBaseUrlAndAnOptionalTemperature_ButNoInlineKey()
    {
        var node = schema.NodeSchema("LlmQuery@1");
        var properties = node["properties"]!.AsObject();

        foreach (var expected in new[]
                 {
                     "provider", "baseUrl", "apiKeyConfigurationName", "model", "temperature", "responseFormat",
                     "jsonSchema", "identity", "mcpServerUrl", "mcpToolNames", "maxToolResultChars", "usageOutputPath"
                 })
        {
            Assert.True(properties.ContainsKey(expected), expected);
        }

        Assert.False(properties.ContainsKey("apiKey"));
        Assert.False(properties.ContainsKey("mcpDelegateToCaller"));
        Assert.Contains("null", properties["temperature"]!.ToJsonString(), StringComparison.Ordinal);

        var required = node["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToArray() ?? [];
        Assert.DoesNotContain("baseUrl", required);
        Assert.DoesNotContain("temperature", required);
        Assert.DoesNotContain("question", required);
    }

    private static IPipelineConfigurationSerializer Serializer()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        return services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
    }
}

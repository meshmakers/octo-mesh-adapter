using System.ClientModel;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using OpenAI;
// Do NOT add `using OpenAI.Chat;` — it conflicts with Microsoft.Extensions.AI on
// ChatMessage/ChatResponseFormat/ChatRole/ChatOptions. Use the fully-qualified
// OpenAI.Chat.ChatClient at the construction site.

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
/// Builds provider-specific <see cref="IChatClient"/> instances for LlmQuery@1 and
/// resolves the API key from an AiConfiguration entity.
/// </summary>
internal static class LlmClientFactory
{
    /// <summary>
    /// ActivitySource emitted via Microsoft.Extensions.AI OpenTelemetry (gen_ai.* spans).
    /// </summary>
    internal const string ActivitySourceName = "Meshmakers.Octo.Sdk.MeshAdapter.LlmQuery";

    internal static IChatClient Create(
        LlmQueryNodeConfiguration config, string? apiKey, string model)
    {
        return config.Provider switch
        {
            LlmProvider.OpenAiCompatible => CreateOpenAiCompatible(config, apiKey, model),
            LlmProvider.Anthropic => CreateAnthropic(config, apiKey, model),
            _ => throw new ArgumentOutOfRangeException(nameof(config.Provider),
                $"Unsupported provider: {config.Provider}")
        };
    }

    /// <summary>
    /// Reads the API key from the referenced AiConfiguration entity. Keys are never
    /// configured inline on the node. Null is valid for backends without authentication.
    /// </summary>
    internal static string? ResolveApiKey(
        LlmQueryNodeConfiguration config, IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(config.ApiKeyConfigurationName)) return null;

        if (etlContext.GlobalConfiguration.IsDefined(config.ApiKeyConfigurationName))
        {
            var rawJson = etlContext.GlobalConfiguration.GetRawJson(config.ApiKeyConfigurationName);
            var key = JObject.Parse(rawJson).Value<string>("apiKey");
            if (!string.IsNullOrEmpty(key))
            {
                nodeContext.Debug(
                    $"API key loaded from configuration '{config.ApiKeyConfigurationName}'");
                return key;
            }
        }

        nodeContext.Warning(
            $"AiConfiguration '{config.ApiKeyConfigurationName}' not found or has no ApiKey; " +
            "continuing without an API key (only valid for backends without authentication).");
        return null;
    }

    /// <summary>
    /// Resolves the model: the AiConfiguration's aiModel wins over the node's Model.
    /// </summary>
    internal static string ResolveModel(
        LlmQueryNodeConfiguration config, IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        if (!string.IsNullOrEmpty(config.ApiKeyConfigurationName)
            && etlContext.GlobalConfiguration.IsDefined(config.ApiKeyConfigurationName))
        {
            var rawJson = etlContext.GlobalConfiguration.GetRawJson(config.ApiKeyConfigurationName);
            var model = JObject.Parse(rawJson).Value<string>("aiModel");
            if (!string.IsNullOrEmpty(model))
            {
                nodeContext.Debug(
                    $"Model '{model}' loaded from configuration '{config.ApiKeyConfigurationName}'");
                return model;
            }
        }

        if (string.IsNullOrEmpty(config.Model))
        {
            // No hard-coded default: pinned model ids go out of date.
            throw new ArgumentException(
                "AI model is required. Set 'aiModel' on the AiConfiguration (recommended) " +
                "or 'model' on the node.", nameof(config.Model));
        }

        return config.Model;
    }

    private static IChatClient CreateOpenAiCompatible(
        LlmQueryNodeConfiguration config, string? apiKey, string model)
    {
        // The OpenAI SDK requires a non-empty credential even for auth-less backends.
        var credential = new ApiKeyCredential(apiKey ?? "unused");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        return new OpenAI.Chat.ChatClient(
                model: model,
                credential: credential,
                options: options)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: ActivitySourceName)
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = config.MaxToolRounds)
            .Build();
    }

    private static IChatClient CreateAnthropic(
        LlmQueryNodeConfiguration config, string? apiKey, string model)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                "Anthropic provider requires an API key. Set ApiKeyConfigurationName " +
                "to reference an AiConfiguration entity that holds it.");
        }

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            client = (Anthropic.AnthropicClient)client
                .WithOptions(o => o with { BaseUrl = config.BaseUrl });
        }

        return client
            .AsIChatClient(model)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: ActivitySourceName)
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = config.MaxToolRounds)
            .Build();
    }
}

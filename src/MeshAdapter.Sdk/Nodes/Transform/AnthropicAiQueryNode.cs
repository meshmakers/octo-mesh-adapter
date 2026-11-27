using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
///     <c>AnthropicAiQuery@1</c>: a thin shim over the shared <see cref="LlmQueryEngine" />. The node maps
///     its configuration to <see cref="LlmQueryOptions" /> and supplies the chat client of the Anthropic
///     Messages API (<see cref="AnthropicMessagesChatClient" />); everything the node does happens in the
///     engine. The requests on the wire are pinned byte for byte by
///     <c>AnthropicAiQueryNodeGoldenRequestTests</c>.
/// </summary>
[NodeConfiguration(typeof(AnthropicAiQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class AnthropicAiQueryNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    IServiceAccountTokenService serviceAccountTokenService,
    IOptions<MeshAdapterConfiguration>? meshAdapterConfiguration = null)
    : IPipelineNode
{
    /// <summary>The node as pipeline authors write it; part of the node's warnings.</summary>
    internal const string NodeDisplayName = "AnthropicAiQuery@1";

    /// <summary>The client name the node has always announced to the MCP server.</summary>
    internal const string McpClientName = "OctoMesh-AnthropicAiQueryNode";

    private readonly LlmQueryEngine _engine = new(etlContext, httpClientFactory, serviceAccountTokenService,
        apiKey => new AnthropicMessagesChatClient(httpClientFactory, apiKey));

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<AnthropicAiQueryNodeConfiguration>();
        if (config.KeySource != LlmKeySource.Central)
        {
            await _engine.RunAsync(ToOptions(config), dataContext, nodeContext, next);
            return;
        }

        // AB#8691: the central key. Everything that could fail before a request fails here, whatever
        // continueOnError says: a node that asked for the central key never falls back to a tenant key.
        var gateway = ResolveGatewayAccess(config, meshAdapterConfiguration?.Value, etlContext);
        var token = await etlContext.GetLlmGatewayTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                $"{NodeDisplayName} with keySource Central needs a token of the pipeline's service account for the " +
                "AI service's LLM gateway (scope ai.llm.invoke), and none could be acquired. The pipeline needs a " +
                "service account that is allowed the scope; see the adapter log for the cause. Nothing was sent.");
        }

        nodeContext.RegisterSecret(token);
        nodeContext.Info($"Using the central Meshmakers key through the AI service's LLM gateway ({gateway.BaseUrl})");
        var engine = new LlmQueryEngine(etlContext, httpClientFactory, serviceAccountTokenService,
            credential => new AnthropicMessagesChatClient(httpClientFactory, credential, gateway: gateway));
        await engine.RunAsync(ToOptions(config) with { CentralGatewayToken = token }, dataContext, nodeContext, next);
    }

    /// <summary>
    ///     The gateway of a node with <c>keySource: Central</c>: the adapter's <c>LlmGatewayUrl</c> (an absolute
    ///     http or https URL) and the caller reference of this execution. Refuses an inline <c>apiKey</c>, so a key in
    ///     the pipeline definition is never silently ignored, and a missing or malformed gateway URL (fail closed).
    /// </summary>
    /// <exception cref="InvalidOperationException">Configuration not usable; nothing has been sent.</exception>
    internal static LlmGatewayAccess ResolveGatewayAccess(AnthropicAiQueryNodeConfiguration config,
        MeshAdapterConfiguration? adapterConfiguration, IMeshEtlContext etlContext)
    {
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            throw new InvalidOperationException(
                $"{NodeDisplayName}: 'apiKey' cannot be combined with keySource Central — the central key is held by " +
                "the AI service and no key may be configured on the node. Remove 'apiKey'.");
        }

        var url = adapterConfiguration?.LlmGatewayUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo) || !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            throw new InvalidOperationException(
                $"{NodeDisplayName} with keySource Central needs the AI service's LLM gateway, but this adapter has " +
                "no valid 'Adapter:LlmGatewayUrl' (OCTO_ADAPTER__LLMGATEWAYURL). Nothing was sent.");
        }

        var callerRef = $"pipeline:{etlContext.PipelineRtEntityId.RtId}/execution:{etlContext.PipelineExecutionId:N}";
        return new LlmGatewayAccess(baseUrl, LlmGatewayAccess.IsValidCallerRef(callerRef) ? callerRef : null);
    }

    /// <summary>The node's configuration as the engine reads it: every value as configured.</summary>
    internal static LlmQueryOptions ToOptions(AnthropicAiQueryNodeConfiguration config)
    {
        return new LlmQueryOptions
        {
            NodeDisplayName = NodeDisplayName,
            McpClientName = McpClientName,
            DefaultSystemPrompt = AnthropicAiQueryNodeConfiguration.DefaultSystemPrompt,
            Path = config.Path,
            TargetPath = config.TargetPath,
            TargetValueWriteMode = config.TargetValueWriteMode,
            TargetValueKind = config.TargetValueKind,
            DocumentMode = config.DocumentMode,
            ApiKeyConfigurationName = config.ApiKeyConfigurationName,
            ApiKey = config.ApiKey,
            Model = config.Model,
            PromptTemplateName = config.PromptTemplateName,
            Question = config.Question,
            DataPaths = config.DataPaths,
            SystemPrompt = config.SystemPrompt,
            MaxTokens = config.MaxTokens,
            Temperature = config.Temperature,
            ResponseFormat = config.ResponseFormat,
            IncludeRawResponse = config.IncludeRawResponse,
            RawResponseOutputPath = config.RawResponseOutputPath,
            ContinueOnError = config.ContinueOnError,
            McpServerUrl = config.McpServerUrl,
            MaxToolRounds = config.MaxToolRounds,
            McpToolNames = config.McpToolNames,
            McpServiceAccountConfigName = config.McpServiceAccountConfigName,
            McpDelegateToCaller = config.McpDelegateToCaller,
            DelegatesToCaller = config.DelegatesToCaller,
            ConversationHistoryPath = config.ConversationHistoryPath,
            JsonFormatSample = config.JsonFormatSample,
            JsonSchema = config.JsonSchema,
            MaxJsonRepairAttempts = config.MaxJsonRepairAttempts,
            UsageOutputPath = config.UsageOutputPath,
            CacheSource = config.CacheSource
        };
    }

    // The members below are what the node's tests call. They hold no logic: each forwards to the
    // engine or to one of its helpers, so the tests of this node keep testing the code that runs.

    /// <inheritdoc cref="LlmMcpSession.EnsureAccessTokenAsync" />
    internal Task<bool> EnsureMcpAccessTokenAsync(AnthropicAiQueryNodeConfiguration config,
        INodeContext nodeContext)
    {
        return _engine.McpSession.EnsureAccessTokenAsync(config.McpServiceAccountConfigName,
            config.DelegatesToCaller, nodeContext);
    }

    /// <inheritdoc cref="LlmMcpSession.AddAuthHeader" />
    internal void AddMcpAuthHeader(HttpRequestMessage request)
    {
        _engine.McpSession.AddAuthHeader(request);
    }

    /// <inheritdoc cref="LlmPromptBuilder.NoToolsSystemPromptSuffix" />
    internal const string NoToolsSystemPromptSuffix = LlmPromptBuilder.NoToolsSystemPromptSuffix;

    /// <inheritdoc cref="LlmPromptBuilder.ResolveQuestion" />
    internal static string ResolveQuestion(AnthropicAiQueryNodeConfiguration config,
        AiPromptTemplateSettings? template)
    {
        return LlmPromptBuilder.ResolveQuestion(ToOptions(config), template);
    }

    /// <inheritdoc cref="LlmPromptBuilder.ResolveSystemPrompt" />
    internal static string ResolveSystemPrompt(AnthropicAiQueryNodeConfiguration config,
        AiPromptTemplateSettings? template)
    {
        return LlmPromptBuilder.ResolveSystemPrompt(ToOptions(config), template);
    }

    /// <inheritdoc cref="LlmPromptBuilder.BuildEffectiveSystemPrompt" />
    internal static string BuildEffectiveSystemPrompt(string systemPrompt, bool mcpConfigured, int mcpToolCount)
    {
        return LlmPromptBuilder.BuildEffectiveSystemPrompt(systemPrompt, mcpConfigured, mcpToolCount);
    }

    /// <inheritdoc cref="LlmPromptBuilder.ResolveMainContent" />
    internal static string? ResolveMainContent(IDataContext dataContext, string? path)
    {
        return LlmPromptBuilder.ResolveMainContent(dataContext, path);
    }

    /// <inheritdoc cref="LlmPromptBuilder.BuildUserPrompt" />
    internal static string BuildUserPrompt(string question, string context, string responseFormat,
        string jsonExample, string? jsonSchema = null)
    {
        return LlmPromptBuilder.BuildUserPrompt(question, context, responseFormat, jsonExample, jsonSchema);
    }

    /// <inheritdoc cref="LlmPromptBuilder.BuildUserPromptParts" />
    internal static (string SourcePart, string InstructionPart) BuildUserPromptParts(string question,
        string context, string responseFormat, string jsonExample, string? jsonSchema = null,
        int? cacheableContextLength = null)
    {
        return LlmPromptBuilder.BuildUserPromptParts(question, context, responseFormat, jsonExample, jsonSchema,
            cacheableContextLength);
    }

    /// <inheritdoc cref="LlmQueryEngine.ExpectsMcpTools" />
    internal static bool ExpectsMcpTools(AnthropicAiQueryNodeConfiguration config)
    {
        return LlmQueryEngine.ExpectsMcpTools(ToOptions(config));
    }

    /// <inheritdoc cref="LlmQueryEngine.HasMcpIdentity" />
    internal static bool HasMcpIdentity(AnthropicAiQueryNodeConfiguration config)
    {
        return LlmQueryEngine.HasMcpIdentity(ToOptions(config));
    }

    /// <inheritdoc cref="LlmQueryEngine.ResolveModel" />
    internal static string ResolveModel(AnthropicAiQueryNodeConfiguration config,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        return LlmQueryEngine.ResolveModel(ToOptions(config), etlContext, nodeContext, template);
    }

    /// <inheritdoc cref="LlmQueryEngine.ResolveMaxTokens" />
    internal static int ResolveMaxTokens(AnthropicAiQueryNodeConfiguration config,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        return LlmQueryEngine.ResolveMaxTokens(ToOptions(config), etlContext, nodeContext, template);
    }

    /// <inheritdoc cref="LlmQueryEngine.ResolveTemperature" />
    internal static double ResolveTemperature(AnthropicAiQueryNodeConfiguration config,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        return LlmQueryEngine.ResolveTemperature(ToOptions(config), etlContext, nodeContext, template) ??
               config.Temperature;
    }

    /// <inheritdoc cref="AnthropicMessagesChatClient.BuildRequestBody" />
    internal static Dictionary<string, object> BuildRequestBody(string model, int maxTokens, double temperature,
        string? systemPrompt, List<object> messages, List<JsonElement>? mcpTools, JsonNode? outputSchema = null)
    {
        return AnthropicMessagesChatClient.BuildRequestBody(model, maxTokens, temperature, systemPrompt, messages,
            mcpTools, outputSchema);
    }

    /// <inheritdoc cref="AnthropicMessagesChatClient.BuildAnthropicApiErrorMessage" />
    internal static string BuildAnthropicApiErrorMessage(HttpStatusCode statusCode, string responseBody)
    {
        return AnthropicMessagesChatClient.BuildAnthropicApiErrorMessage(statusCode, responseBody);
    }

    /// <inheritdoc cref="AnthropicMessagesChatClient.IsContextLengthError" />
    internal static bool IsContextLengthError(string? responseBody)
    {
        return AnthropicMessagesChatClient.IsContextLengthError(responseBody);
    }

    /// <inheritdoc cref="LlmResponseProcessor.BuildTruncationMessage" />
    internal static string BuildTruncationMessage(int maxTokens)
    {
        return LlmResponseProcessor.BuildTruncationMessage(maxTokens);
    }

    /// <inheritdoc cref="LlmResponseProcessor.ContainsSimulatedToolCalls" />
    internal static bool ContainsSimulatedToolCalls(string? text)
    {
        return LlmResponseProcessor.ContainsSimulatedToolCalls(text);
    }

    /// <inheritdoc cref="LlmResponseProcessor.ProcessResponse" />
    internal static object ProcessResponse(string aiResponse, string responseFormat, INodeContext nodeContext)
    {
        return LlmResponseProcessor.ProcessResponse(aiResponse, responseFormat, nodeContext);
    }

    /// <inheritdoc cref="LlmResponseProcessor.ExtractJsonFromText" />
    internal static string? ExtractJsonFromText(string text)
    {
        return LlmResponseProcessor.ExtractJsonFromText(text);
    }
}

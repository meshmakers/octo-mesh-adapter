using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
///     <c>LlmQuery@1</c>: the provider-agnostic LLM query node, a thin shim over the shared
///     <see cref="LlmQueryEngine" /> like <c>AnthropicAiQuery@1</c>. The node maps its configuration to
///     <see cref="LlmQueryOptions" /> and supplies the chat client of the configured provider at the
///     configured base URL; prompt, MCP, tool loop, answer and usage are the engine's.
/// </summary>
/// <remarks>
///     The node and its configuration are lifted from the LlmQuery prototype by Philipp Schwaab; the
///     prototype's own query code is replaced by the engine.
/// </remarks>
[NodeConfiguration(typeof(LlmQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class LlmQueryNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    IServiceAccountTokenService serviceAccountTokenService)
    : IPipelineNode
{
    /// <summary>The node as pipeline authors write it; part of the node's warnings.</summary>
    internal const string NodeDisplayName = "LlmQuery@1";

    /// <summary>The client name the node announces to the MCP server.</summary>
    internal const string McpClientName = "OctoMesh-LlmQueryNode";

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<LlmQueryNodeConfiguration>();
        // Before any request: a base URL that breaks the rule fails the node, whatever continueOnError says.
        var baseUrl = ResolveBaseUrl(config, etlContext, nodeContext);
        var engine = new LlmQueryEngine(etlContext, httpClientFactory, serviceAccountTokenService,
            CreateChatClientFactory(config, httpClientFactory, baseUrl));
        await engine.RunAsync(ToOptions(config), dataContext, nodeContext, next);
    }

    /// <summary>The chat client of the configured provider at the resolved base URL, for an API key.</summary>
    internal static Func<string, IChatClient> CreateChatClientFactory(LlmQueryNodeConfiguration config,
        IHttpClientFactory httpClientFactory, Uri? baseUrl)
    {
        return config.Provider switch
        {
            LlmProvider.Anthropic => apiKey => new AnthropicMessagesChatClient(httpClientFactory, apiKey, baseUrl),
            _ => throw new NotSupportedException($"Provider '{config.Provider}' is not supported.")
        };
    }

    /// <summary>
    ///     The one place that decides where the provider is reached: the <c>baseUrl</c> of the AiConfiguration
    ///     named by <c>apiKeyConfigurationName</c> (the one that holds the key), read with the engine's guarded
    ///     read and checked by the rule of <see cref="LlmBaseUrl" />. <c>null</c> (the provider's public endpoint)
    ///     when it is absent or empty.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value breaks the rule; nothing has been sent.</exception>
    internal static Uri? ResolveBaseUrl(LlmQueryNodeConfiguration config, IMeshEtlContext etlContext,
        INodeContext nodeContext)
    {
        var value = LlmQueryEngine.ReadAiConfigurationString(config.ApiKeyConfigurationName, BaseUrlProperty,
            etlContext, nodeContext);
        try
        {
            var baseUrl = LlmBaseUrl.Parse(value);
            if (baseUrl != null)
            {
                nodeContext.Debug($"Provider base URL loaded from configuration '{config.ApiKeyConfigurationName}'");
            }

            return baseUrl;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"[{nodeContext.NodePath}]: AiConfiguration '{config.ApiKeyConfigurationName}': {ex.Message} " +
                "No request was sent.", ex);
        }
    }

    /// <summary>The key of the provider's endpoint in the AiConfiguration payload.</summary>
    internal const string BaseUrlProperty = "baseUrl";

    /// <summary>The node's configuration as the engine reads it.</summary>
    internal static LlmQueryOptions ToOptions(LlmQueryNodeConfiguration config)
    {
        var delegatesToCaller = config.Identity == NodeExecutionIdentity.Caller;
        return new LlmQueryOptions
        {
            NodeDisplayName = NodeDisplayName,
            McpClientName = McpClientName,
            DefaultSystemPrompt = LlmQueryNodeConfiguration.DefaultSystemPrompt,
            Path = config.Path,
            TargetPath = config.TargetPath,
            TargetValueWriteMode = config.TargetValueWriteMode,
            TargetValueKind = config.TargetValueKind,
            DocumentMode = config.DocumentMode,
            ApiKeyConfigurationName = config.ApiKeyConfigurationName,
            // No inline key: the key comes from the AiConfiguration only.
            ApiKey = null,
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
            McpDelegateToCaller = delegatesToCaller,
            DelegatesToCaller = delegatesToCaller,
            ConversationHistoryPath = config.ConversationHistoryPath,
            JsonFormatSample = config.JsonFormatSample,
            JsonSchema = config.JsonSchema,
            MaxJsonRepairAttempts = config.MaxJsonRepairAttempts,
            UsageOutputPath = config.UsageOutputPath,
            CacheSource = config.CacheSource,
            MaxToolResultChars = config.MaxToolResultChars ?? LlmQueryNodeConfiguration.DefaultMaxToolResultChars
        };
    }
}

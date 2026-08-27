using System.ClientModel;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Queries an LLM with pipeline data. Provider construction, prompt assembly, JSON
/// response processing and MCP tool handling live in <c>Internal/Llm*</c> helpers;
/// this class orchestrates the call.
/// </summary>
[NodeConfiguration(typeof(LlmQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class LlmQueryNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IServiceAccountTokenService serviceAccountTokenService)
    : IPipelineNode
{
    /// <summary>
    /// ActivitySource for gen_ai.* spans; use in OTel collector configuration.
    /// </summary>
    internal const string ActivitySourceName = LlmClientFactory.ActivitySourceName;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<LlmQueryNodeConfiguration>();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var ct = timeoutCts.Token;

        // MCP clients must stay alive for the duration of the LLM call (the tool loop
        // invokes them) and are disposed in the finally block — stdio clients own a
        // spawned subprocess, HTTP/SSE clients an open connection.
        var mcpClients = new List<McpClient>();

        try
        {
            if (string.IsNullOrEmpty(config.Question))
            {
                throw new ArgumentException("Question is required", nameof(config.Question));
            }

            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(
                    nodeContext, nameof(config.Path));
            }

            // Temperature and TopP are alternative sampling controls; Anthropic rejects
            // requests that set both. Enforced uniformly across providers.
            if (config.Temperature is not null && config.TopP is not null)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(
                    nodeContext,
                    new ArgumentException(
                        $"Sampling configuration error: Temperature ({config.Temperature}) and " +
                        $"TopP ({config.TopP}) are mutually exclusive — set one, leave the other null."));
            }

            var apiKey = LlmClientFactory.ResolveApiKey(config, etlContext, nodeContext);
            var model = LlmClientFactory.ResolveModel(config, etlContext, nodeContext);

            nodeContext.Debug(
                $"Starting LlmQuery (provider: {config.Provider}, model: {model})");

            // GetKind first to distinguish "path not present" from "present but empty".
            string? mainContent = null;
            if (dataContext.GetKind(config.Path) is not DataKind.Undefined)
            {
                mainContent = dataContext.Get<string>(config.Path);
            }

            if (string.IsNullOrEmpty(mainContent))
            {
                nodeContext.Warning($"No content found at path: {config.Path}");
                await next(dataContext, nodeContext);
                return;
            }

            var wantsJson = config.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase);

            // JSON mode sanitizes the double-quote glyph family inside content values so the
            // model cannot copy a JSON string terminator into its output when quoting verbatim.
            var context = LlmPromptBuilder.BuildContext(
                mainContent, config.DataPaths, dataContext, nodeContext, wantsJson);
            var userPrompt = LlmPromptBuilder.BuildUserPrompt(
                config.Question, context, config.ResponseFormat, config.JsonFormatSample);

            var client = LlmClientFactory.Create(config, apiKey, model);

            var mcpServers = McpServerResolver.Resolve(config.McpConfigurationNames, etlContext, nodeContext);
            if (mcpServers.Count > 0)
            {
                // octo-mcp-service requires a bearer on every request (AB#4315); tokens are
                // cached per configuration name in the singleton provider.
                mcpServers = await McpServerResolver.ApplyServiceAccountTokensAsync(
                    mcpServers, serviceAccountTokenService, etlContext, nodeContext, ct);
            }

            var mcpTools = mcpServers.Count > 0
                ? await LlmMcpTools.LoadAsync(mcpServers, mcpClients, nodeContext, ct)
                : (IList<AIFunction>)Array.Empty<AIFunction>();

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, config.SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            if (!string.IsNullOrEmpty(config.ConversationHistoryPath))
            {
                var history = LlmPromptBuilder.LoadHistory(
                    config.ConversationHistoryPath, dataContext, nodeContext);
                if (history.Count > 0)
                {
                    messages.InsertRange(1, history);
                }
            }

            var options = BuildChatOptions(config, model, wantsJson, mcpTools, nodeContext);

            nodeContext.Debug($"Calling LLM with {context.Length} characters of context");

            var response = await client.GetResponseAsync(messages, options, ct);
            var aiResponse = response.Text;

            LlmMcpTools.LogToolCalls(response, mcpTools.Count, nodeContext);

            if (string.IsNullOrEmpty(aiResponse))
            {
                throw new InvalidOperationException("Empty response from LLM");
            }

            nodeContext.Info(
                $"Received LLM response with {aiResponse.Length} characters " +
                $"({response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount} tokens)");

            var processedResponse = await LlmJsonResponseProcessor.ProcessWithRepairAsync(
                aiResponse, config, client, options, nodeContext, ct);

            dataContext.Set(
                config.TargetPath,
                processedResponse,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode
            );

            if (config.IncludeRawResponse)
            {
                dataContext.Set(
                    config.RawResponseOutputPath ?? "$.RawAiResponse",
                    aiResponse,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode
                );
            }

            nodeContext.Info("LlmQuery completed successfully");
        }
        catch (OperationCanceledException oce) when (timeoutCts.IsCancellationRequested)
        {
            // Own timeout budget elapsed (provider call + tool loop), not an upstream abort.
            nodeContext.Error(
                $"LlmQuery timed out after {config.TimeoutSeconds}s (provider call + tool loop " +
                "exceeded the budget). Increase TimeoutSeconds, lower MaxToolRounds, or check " +
                "MCP server latency / provider rate limits.");
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, oce);
        }
        catch (OperationCanceledException)
        {
            // Upstream cancellation — ContinueOnError does not apply.
            throw;
        }
        catch (ClientResultException cre)
        {
            // Surface the response body; the SDK's message hides the actionable detail.
            var body = cre.GetRawResponse()?.Content?.ToString();
            nodeContext.Error(
                $"LLM provider request failed (HTTP {cre.Status}): {body ?? cre.Message}");

            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, cre);
            }
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during LlmQuery ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            await LlmMcpTools.DisposeAsync(mcpClients, nodeContext);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Builds ChatOptions. response_format is only sent when JSON is requested and no
    /// tools are attached (strict OpenAI-compatible backends reject the combination);
    /// with a jsonSchema, the schema is enforced server-side via structured outputs.
    /// </summary>
    private static ChatOptions BuildChatOptions(
        LlmQueryNodeConfiguration config, string model, bool wantsJson,
        IList<AIFunction> mcpTools, INodeContext nodeContext)
    {
        var hasTools = mcpTools.Count > 0;

        if (wantsJson && hasTools)
        {
            nodeContext.Warning(
                "responseFormat=json is incompatible with MCP tools on most providers; " +
                "sending the request without response_format and relying on the system " +
                "prompt to enforce JSON." +
                (string.IsNullOrWhiteSpace(config.JsonSchema)
                    ? string.Empty
                    : " The configured jsonSchema is NOT enforced in tool mode."));
        }

        JsonElement? responseSchema = null;
        if (wantsJson && !hasTools && !string.IsNullOrWhiteSpace(config.JsonSchema))
        {
            try
            {
                responseSchema = JsonSerializer.Deserialize<JsonElement>(config.JsonSchema);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"jsonSchema is not valid JSON: {ex.Message}");
            }
        }
        else if (wantsJson && !hasTools && config.Provider == LlmProvider.Anthropic)
        {
            // Anthropic has no plain JSON mode; without a schema, JSON is prompt-only.
            nodeContext.Warning(
                "Anthropic without jsonSchema: JSON is prompt-enforced only. " +
                "Set jsonSchema for guaranteed structured output.");
        }

        return new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = config.MaxTokens,
            Temperature = (float?)config.Temperature,
            TopP = config.TopP,
            TopK = config.TopK,
            ResponseFormat = wantsJson && !hasTools
                ? responseSchema is not null
                    ? ChatResponseFormat.ForJsonSchema(responseSchema.Value,
                        schemaName: "llm_query_response")
                    : ChatResponseFormat.Json
                : null,
            Tools = hasTools ? [..mcpTools] : null
        };
    }
}

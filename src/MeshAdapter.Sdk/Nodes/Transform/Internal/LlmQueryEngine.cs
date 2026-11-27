using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     One LLM query of a pipeline node, from the node's options to the value in the data context:
///     settings from the AiConfiguration and the prompt template, the prompt, the MCP tools, the tool
///     loop, the answer (plain, JSON, or JSON guaranteed by a schema with repair calls) and the usage.
///     This is the body of <c>AnthropicAiQueryNode</c>, moved here unchanged so that every LLM query
///     node runs it; the node maps its configuration to <see cref="LlmQueryOptions" /> and supplies the
///     chat client of its provider.
/// </summary>
/// <remarks>
///     The provider is behind <see cref="IChatClient" />: the engine sends chat messages and options and
///     reads answer, finish reason, tool calls and usage; it knows no provider wire format. The tool
///     loop is the engine's own, so tool calls, their cap and their log are the same for every provider.
///     Guarded configuration read, history with content parts, the cut that never splits a surrogate
///     pair and the tool-call log without argument values are lifted from the LlmQuery prototype by Philipp Schwaab.
/// </remarks>
internal sealed class LlmQueryEngine(
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    IServiceAccountTokenService serviceAccountTokenService,
    Func<string, IChatClient> chatClientFactory)
{
    /// <summary>The cap of <see cref="LlmQueryOptions.MaxToolResultChars" /> a node does not set.</summary>
    internal const int DefaultMaxToolResultChars = 50_000;

    /// <summary>
    ///     Key in <see cref="AIContent.AdditionalProperties" /> of a text part of the user message:
    ///     <c>true</c> asks the provider for a prompt-cache breakpoint after that part. A provider
    ///     without prompt caching ignores it.
    /// </summary>
    internal const string CacheBreakpointProperty = "cacheBreakpoint";

    /// <summary>
    ///     Key in <see cref="ChatOptions.AdditionalProperties" />: the temperature as the node resolved it, a
    ///     <see cref="double" />. <see cref="ChatOptions.Temperature" /> is a <see cref="float" />; a provider
    ///     client that sends the temperature as text reads the exact value here.
    /// </summary>
    internal const string ExactTemperatureProperty = "temperature";

    /// <summary>The MCP session of the node this engine serves (token, handshake, tool calls).</summary>
    internal LlmMcpSession McpSession { get; } = new(etlContext, httpClientFactory, serviceAccountTokenService);

    public async Task RunAsync(LlmQueryOptions options, IDataContext dataContext, INodeContext nodeContext,
        NodeDelegate next)
    {
        // AB#8658: usage of every API call of this execution (tool rounds and repair calls included).
        var usage = new AiUsageAccumulator();
        string? usageModel = null;
        // True once the rest of the pipeline has been handed the data from inside the try block: what a later
        // node throws is then its own failure, not one of this query.
        var handedOver = false;

        try
        {
            // Resolve API key: prefer ApiKeyConfigurationName over direct ApiKey
            var apiKey = ResolveApiKey(options, etlContext, nodeContext) ?? string.Empty;
            // AB#5538: the API key is masked in every diagnostic output of this execution.
            nodeContext.RegisterSecret(apiKey);
            if (string.IsNullOrEmpty(apiKey) && !options.HasSealedProviderEndpoint)
            {
                throw new ArgumentException(
                    options.HasInlineApiKeyOption
                        ? "API key is required. Set either 'apiKeyConfigurationName' (recommended) or 'apiKey' on the node."
                        : "API key is required. Set 'apiKeyConfigurationName' to an AiConfiguration that holds an " +
                          "'apiKey' or a 'providerEndpoint'.",
                    nameof(options.ApiKey));
            }

            // AB#8498: prompt by name. The template is part of the pipeline's configuration
            // hand-over (Uses association), so a missing one fails before any paid call.
            var template = AiPromptTemplateResolver.Resolve(etlContext.GlobalConfiguration, options.PromptTemplateName);
            if (template != null)
            {
                nodeContext.Info(template.TemplateVersion.HasValue
                    ? $"Using AiPromptTemplate '{template.Name}' (version {template.TemplateVersion})"
                    : $"Using AiPromptTemplate '{template.Name}'");
            }

            var question = LlmPromptBuilder.ResolveQuestion(options, template);
            var baseSystemPrompt = LlmPromptBuilder.ResolveSystemPrompt(options, template);

            // AB#8498: guaranteed JSON. The schema is parsed before any API call so a malformed
            // jsonSchema fails the node at once instead of after a paid request.
            var jsonSchemaMode = UsesJsonSchema(options, nodeContext);
            var schema = jsonSchemaMode ? AiJsonSchemaResponseProcessor.ParseSchema(options.JsonSchema!) : null;
            var apiSchema = jsonSchemaMode ? AiJsonSchemaResponseProcessor.BuildApiSchema(options.JsonSchema!) : null;
            // The chat client gets the schema that is sent: the prepared copy, not the authored one.
            var responseFormat = apiSchema == null
                ? null
                : ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(apiSchema));

            // Resolve MCP server URL from AiConfiguration if available
            var mcpServerUrl = ResolveMcpServerUrl(options, etlContext, nodeContext);

            // Resolve the model: template (AB#8498) -> AiConfiguration (aiModel) -> node config
            var model = ResolveModel(options, etlContext, nodeContext, template);
            usageModel = model;

            // Resolve max tokens and temperature: template -> AiConfiguration -> node config
            var maxTokens = ResolveMaxTokens(options, etlContext, nodeContext, template);
            var sampling = new RunSampling(ResolveTemperature(options, etlContext, nodeContext, template),
                options.NodeDisplayName, nodeContext, options.TemperatureIsOptional);

            nodeContext.Debug("Starting Anthropic AI query");

            // Get the main content from the configured path (optional when using MCP tools).
            var mainContent = LlmPromptBuilder.ResolveMainContent(dataContext, options.Path);

            if (string.IsNullOrEmpty(mainContent) && string.IsNullOrEmpty(mcpServerUrl))
            {
                nodeContext.Warning($"No content found at path: {options.Path}");
                handedOver = true;
                await next(dataContext, nodeContext);
                return;
            }

            // Build the context from additional data paths
            var contextBuilder = new StringBuilder();
            if (!string.IsNullOrEmpty(mainContent))
            {
                contextBuilder.AppendLine("Main Content:");
                contextBuilder.AppendLine(mainContent);
                contextBuilder.AppendLine();
            }

            // AB#8658: with cacheSource only the main content is cached — dataPaths and the caller below
            // change from call to call (e.g. the draft a verification call checks) and would turn every
            // call into a cache miss if they were inside the cached block.
            var cacheableContextLength = contextBuilder.Length;

            if (options.DataPaths is { Length: > 0 })
            {
                contextBuilder.AppendLine("Additional Context:");

                foreach (var dataPath in options.DataPaths)
                {
                    try
                    {
                        var kind = dataContext.GetKind(dataPath);
                        if (kind is DataKind.Undefined)
                        {
                            continue;
                        }

                        contextBuilder.AppendLine($"Data from {dataPath}:");

                        if (kind == DataKind.String)
                        {
                            contextBuilder.AppendLine(dataContext.Get<string>(dataPath));
                        }
                        else
                        {
                            // Non-string scalars and structured values are rendered as indented
                            // JSON for the AI prompt. Get<object?> materializes objects/arrays to
                            // a JsonElement which re-serializes equivalently to the former node's
                            // JsonNode.ToJsonString(WriteIndented).
                            var value = dataContext.Get<object?>(dataPath);
                            contextBuilder.AppendLine(
                                JsonSerializer.Serialize(value, LlmPromptBuilder.IndentedPromptOptions));
                        }

                        contextBuilder.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        nodeContext.Warning($"Could not retrieve data from path {dataPath}: {ex.Message}");
                    }
                }
            }

            // AB#5136 (Strang B): when the trigger resolved a verified caller — the bearer of
            // FromHttpRequest@2, or a channel sender bound via CallerBinding on FromSignal/FromEmail/
            // FromMicrosoftGraph(Email) — surface that identity to the model uniformly, independent
            // of any DataPaths. The channel triggers only carry the caller on the execution
            // (ExecutePipelineOptions.VerifiedPrincipal, for data authorization); unlike
            // FromHttpRequest@2 they never write $.principal into the data root, so without this the
            // assistant cannot tell the user who they are over Signal/e-mail/Teams. This is the single
            // source of "who am I talking to". Absent when the pipeline runs anonymous / as the
            // service account, in which case nothing is added.
            // Only surface a caller that carries a human identity (name or e-mail). A principal with
            // just a SubjectId and roles is a service account — e.g. the shared answerer pipeline's
            // own identity (AB#5045), which must NOT be shown as "the calling user". For that
            // stager→answerer topology the real caller travels as data instead (WriteVerifiedCaller@1
            // in the stager → $.input.caller here), read via DataPaths.
            if (etlContext.VerifiedPrincipal is { } caller
                && (caller.Name is not null || caller.Email is not null))
            {
                contextBuilder.AppendLine(
                    "Calling user (the verified identity of the person you are talking to — address them " +
                    "personally, treat \"ich\"/\"meine\" as this user, and answer any question about who they " +
                    "are, their name, username or e-mail directly from here, never via tools. The data you " +
                    "can query is already scoped to what this user may see):");
                if (caller.Name is not null)
                {
                    contextBuilder.AppendLine($"  name: {caller.Name}");
                }

                if (caller.Email is not null)
                {
                    contextBuilder.AppendLine($"  email: {caller.Email}");
                }

                if (caller.Roles is { Count: > 0 })
                {
                    contextBuilder.AppendLine($"  roles: {string.Join(", ", caller.Roles)}");
                }

                contextBuilder.AppendLine($"  tenant: {caller.TenantId ?? etlContext.TenantId}");
                contextBuilder.AppendLine();
            }

            var fullContext = contextBuilder.ToString();
            nodeContext.Debug($"Querying Claude with {fullContext.Length} characters of context");

            // Build the prompt. With cacheSource (AB#8658) the source part becomes its own content
            // block carrying a cache breakpoint; the text the model reads is the same either way.
            var (sourcePart, instructionPart) =
                LlmPromptBuilder.BuildUserPromptParts(question, fullContext, options.ResponseFormat,
                    options.JsonFormatSample, jsonSchemaMode ? options.JsonSchema : null, cacheableContextLength);
            var userMessage = options.CacheSource
                ? new ChatMessage(ChatRole.User,
                [
                    new TextContent(sourcePart)
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary { [CacheBreakpointProperty] = true }
                    },
                    new TextContent(instructionPart)
                ])
                : new ChatMessage(ChatRole.User, sourcePart + instructionPart);

            // AB#8685: an MCP URL (often inherited from the AiConfiguration's McpServerUrl) without an
            // identity to call it with is skipped instead of called unauthenticated — the MCP service
            // enforces authentication (AB#4315) and only answers 401. One warning names the node and
            // the missing setting; the query runs without tools.
            var mcpSkippedForMissingIdentity = !string.IsNullOrEmpty(mcpServerUrl) && !HasMcpIdentity(options);
            if (mcpSkippedForMissingIdentity)
            {
                nodeContext.Warning(BuildMissingMcpIdentityWarning(options.NodeDisplayName,
                    nodeContext.NodePath.ToString()));
            }

            // Load MCP tools if configured
            List<AITool>? mcpTools = null;
            var mcpAuthorized = false;
            if (!string.IsNullOrEmpty(mcpServerUrl) && !mcpSkippedForMissingIdentity)
            {
                // Authenticate to the MCP server via a ServiceAccountConfiguration (AB#4315).
                // With mcpDelegateToCaller the token instead runs on the calling user's identity and
                // any failure is fatal rather than degrading (AB#5031).
                // AB#8699: a configured service account that yields no token is fail-closed — no MCP
                // call; EnsureAccessTokenAsync has warned and the query runs without tools.
                mcpAuthorized = await McpSession.EnsureAccessTokenAsync(options.McpServiceAccountConfigName,
                    options.DelegatesToCaller, nodeContext);

                if (mcpAuthorized)
                {
                    mcpTools = await McpSession.LoadToolsAsync(mcpServerUrl, etlContext.TenantId,
                        options.McpClientName, nodeContext);

                    // Filter tools if mcpToolNames is specified
                    if (mcpTools != null && options.McpToolNames is { Length: > 0 })
                    {
                        var allowedNames =
                            new HashSet<string>(options.McpToolNames, StringComparer.OrdinalIgnoreCase);
                        mcpTools = mcpTools.Where(t => allowedNames.Contains(t.Name)).ToList();
                    }

                    nodeContext.Info($"Loaded {mcpTools?.Count ?? 0} MCP tools from OctoMesh");
                }
            }

            // AB#8685 / AB#8699: MCP was configured but is not called — no identity configured, or the
            // configured service account yielded no token. Either case has already been warned about once.
            var mcpNotCalled = !string.IsNullOrEmpty(mcpServerUrl) && !mcpAuthorized;

            // AB#4562 follow-up: when MCP was configured but no tools could be loaded (server
            // unreachable, auth rejected, name filter matched nothing), the pipeline's static
            // system prompt still promises tools. Without a correction the model imitates tool
            // calls as text and fabricates their results (observed on prod-1: invented invoice
            // data in the accounting chat). Append an explicit no-tools instruction instead.
            // 5.10.2026: the same correction when the pipeline EXPECTS tools (it delegates to the
            // caller, names a service account or a tool list) but no MCP server URL resolved at
            // all — the AiConfiguration entity of a fresh or local tenant carries no McpServerUrl.
            // That case skipped the suffix entirely, and the accounting chat answered "which
            // invoices are open" with invented tool calls and invented invoices.
            var mcpExpected = ExpectsMcpTools(options);
            var systemPrompt = LlmPromptBuilder.BuildEffectiveSystemPrompt(baseSystemPrompt,
                mcpConfigured: !string.IsNullOrEmpty(mcpServerUrl) || mcpExpected, mcpToolCount: mcpTools?.Count ?? 0);
            // AB#8685: a skipped MCP has already been reported with its own warning — one per execution.
            if (!ReferenceEquals(systemPrompt, baseSystemPrompt) && !mcpNotCalled)
            {
                nodeContext.Warning(string.IsNullOrEmpty(mcpServerUrl)
                    ? "The pipeline expects MCP tools but no MCP server URL is configured (AiConfiguration." +
                      "McpServerUrl / node mcpServerUrl) — appending a no-tools instruction to the system " +
                      "prompt so the model does not simulate tool calls."
                    : "MCP is configured but no tools are available — appending a no-tools instruction " +
                      "to the system prompt so the model does not simulate tool calls.");
            }

            // Load conversation history if configured
            var historyMessages =
                LlmPromptBuilder.LoadHistory(options.ConversationHistoryPath, dataContext, nodeContext);

            // Execute the chat (with optional tool use loop)
            var aiResponse = await ExecuteChatAsync(options, apiKey, model, maxTokens, sampling, systemPrompt,
                mcpNotCalled ? null : mcpServerUrl, userMessage, mcpTools, nodeContext, usage, historyMessages,
                responseFormat);

            // A text answer that CONTAINS tool-call markup is a simulation: real tool use arrives
            // as API content blocks, never as text. Its "results" are invented, so the answer is
            // refused rather than shown (the accounting chat would render invented invoices).
            if (!string.Equals(options.ResponseFormat, "json", StringComparison.OrdinalIgnoreCase) &&
                LlmResponseProcessor.ContainsSimulatedToolCalls(aiResponse))
            {
                throw new InvalidOperationException(LlmResponseProcessor.SimulatedToolCallsMessage);
            }

            if (string.IsNullOrEmpty(aiResponse))
            {
                throw new InvalidOperationException("Empty response from Claude");
            }

            nodeContext.Info($"Received AI response with {aiResponse.Length} characters");

            // Process the response based on format
            // With a jsonSchema the answer is validated and, if needed, repaired by bounded repair
            // calls that never resend the original context (AB#8498); without one, unchanged.
            object processedResponse = jsonSchemaMode
                ? await AiJsonSchemaResponseProcessor.ProcessAsync(aiResponse, options.JsonSchema!, schema!,
                    options.MaxJsonRepairAttempts,
                    repairPrompt => ExecuteJsonRepairCallAsync(apiKey, model, maxTokens, sampling, repairPrompt,
                        responseFormat!, usage),
                    nodeContext)
                : LlmResponseProcessor.ProcessResponse(aiResponse, options.ResponseFormat, nodeContext);

            // Store the processed response
            dataContext.Set(
                options.TargetPath,
                processedResponse,
                options.DocumentMode,
                options.TargetValueKind,
                options.TargetValueWriteMode
            );

            // Store raw response if requested
            if (options.IncludeRawResponse)
            {
                dataContext.Set(
                    options.RawResponseOutputPath ?? "$.RawAiResponse",
                    aiResponse,
                    options.DocumentMode,
                    options.TargetValueKind,
                    options.TargetValueWriteMode
                );
            }

            WriteUsage(options, dataContext, nodeContext, usage, usageModel);
            nodeContext.Info("AI query completed successfully");
        }
        catch (Exception ex) when (!handedOver)
        {
            if (!options.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during AI query: {ex.Message}");
            // The calls that were made are paid even though the query failed.
            WriteUsage(options, dataContext, nodeContext, usage, usageModel);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    ///     Writes the summed usage to <c>usageOutputPath</c> (AB#8658). Nothing without the path or before
    ///     the first API call.
    /// </summary>
    private static void WriteUsage(LlmQueryOptions options, IDataContext dataContext,
        INodeContext nodeContext, AiUsageAccumulator usage, string? model)
    {
        if (string.IsNullOrWhiteSpace(options.UsageOutputPath) || usage.Calls == 0)
        {
            return;
        }

        var usageJson = usage.ToJson(model);
        if (usageJson["estimatedCostUsd"] == null)
        {
            nodeContext.Warning(
                $"No cost estimate for model '{model}': it is not in the adapter's price table. Token counts are written.");
        }

        dataContext.Set(options.UsageOutputPath, usageJson, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        nodeContext.Info(
            $"AI usage: {usage.Calls} call(s), input {usage.InputTokens}, output {usage.OutputTokens}, " +
            $"cache read {usage.CacheReadInputTokens}, cache write {usage.CacheCreationInputTokens} tokens");
    }

    /// <summary>
    ///     True when the node runs in guaranteed-JSON mode (AB#8498): a non-empty <c>jsonSchema</c> with
    ///     <c>responseFormat: json</c>. A schema on a text-format node is ignored with a warning rather
    ///     than failing pipelines that only switched the format.
    /// </summary>
    internal static bool UsesJsonSchema(LlmQueryOptions options, INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(options.JsonSchema))
        {
            return false;
        }

        if (options.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        nodeContext.Warning(
            $"jsonSchema is ignored because responseFormat is '{options.ResponseFormat}', not 'json'.");
        return false;
    }

    /// <summary>
    ///     True when the node's configuration promises tools to the model: it delegates to the
    ///     caller, authenticates to MCP with a service account, or names a tool list. Such a node
    ///     without a resolvable MCP URL must get the no-tools correction like one whose server
    ///     is down.
    /// </summary>
    internal static bool ExpectsMcpTools(LlmQueryOptions options)
    {
        return options.McpDelegateToCaller ||
               !string.IsNullOrEmpty(options.McpServiceAccountConfigName) ||
               options.McpToolNames is { Length: > 0 };
    }

    /// <summary>
    ///     True when the node has an identity to call the MCP server with: a
    ///     <see cref="LlmQueryOptions.McpServiceAccountConfigName" /> (service account,
    ///     AB#4315) or delegation to the caller (<c>mcpDelegateToCaller: true</c> / <c>identity: Caller</c>,
    ///     AB#5031/AB#5127). Without one the node never calls MCP (AB#8685).
    /// </summary>
    internal static bool HasMcpIdentity(LlmQueryOptions options)
    {
        return options.DelegatesToCaller || !string.IsNullOrEmpty(options.McpServiceAccountConfigName);
    }

    /// <summary>
    ///     The warning written once per execution when an MCP server URL resolved but no identity is
    ///     configured, so MCP is skipped (AB#8685). Names the node and the missing setting.
    /// </summary>
    internal static string BuildMissingMcpIdentityWarning(string nodeDisplayName, string nodePath)
    {
        return $"[{nodePath}]: {nodeDisplayName} skips MCP: an MCP server URL is configured (node " +
               "mcpServerUrl or AiConfiguration.McpServerUrl) but no identity to call it with — set " +
               "'mcpServiceAccountConfigName' (service account) or 'mcpDelegateToCaller: true' / " +
               "'identity: Caller' together with 'mcpServiceAccountConfigName'. The query runs without tools.";
    }

    private static string? ResolveApiKey(LlmQueryOptions options, IMeshEtlContext etlContext,
        INodeContext nodeContext)
    {
        // AB#8691: the central key is the gateway's; the run's only credential is the gateway token.
        if (options.CentralGatewayToken != null)
        {
            return options.CentralGatewayToken;
        }

        // A sealed endpoint brings its own key, or none: the configuration's key never goes to its URL.
        if (options.HasSealedProviderEndpoint)
        {
            return options.SealedProviderApiKey ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(options.ApiKeyConfigurationName))
        {
            var key = ReadAiConfigurationString(options.ApiKeyConfigurationName, "apiKey", etlContext, nodeContext);
            if (!string.IsNullOrEmpty(key))
            {
                nodeContext.Debug(
                    $"API key loaded from configuration '{options.ApiKeyConfigurationName}'");
                return key;
            }

            nodeContext.Warning(
                $"AiConfiguration '{options.ApiKeyConfigurationName}' not found or has no ApiKey. " +
                "Ensure the pipeline has a 'Uses' association to the AiConfiguration entity.");
        }

        return options.ApiKey;
    }

    private static string? ResolveMcpServerUrl(LlmQueryOptions options,
        IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        // Try loading from AiConfiguration first
        var url = ReadAiConfigurationString(options.ApiKeyConfigurationName, "mcpServerUrl", etlContext,
            nodeContext);
        if (!string.IsNullOrEmpty(url))
        {
            nodeContext.Debug(
                $"MCP server URL loaded from configuration '{options.ApiKeyConfigurationName}'");
            return url;
        }

        return options.McpServerUrl;
    }

    internal static string ResolveModel(LlmQueryOptions options,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        // AB#8498: a prompt template is tuned for its model, so its DefaultModelId wins.
        if (!string.IsNullOrEmpty(template?.ModelId))
        {
            nodeContext.Debug($"AI model '{template.ModelId}' loaded from AiPromptTemplate '{template.Name}'");
            return template.ModelId;
        }

        // Prefer the model configured on the AiConfiguration entity (aiModel), mirroring how the
        // API key and MCP server URL are resolved from it. Falls back to the node's own Model
        // property (and thus its default) when the AiConfiguration has no aiModel.
        var model = ReadAiConfigurationString(options.ApiKeyConfigurationName, "aiModel", etlContext, nodeContext);
        if (!string.IsNullOrEmpty(model))
        {
            nodeContext.Debug(
                $"AI model '{model}' loaded from configuration '{options.ApiKeyConfigurationName}'");
            return model;
        }

        if (string.IsNullOrEmpty(options.Model))
        {
            // No hard-coded default: a pinned model id goes out of date (e.g. a retired
            // "…-20250514" snapshot returns HTTP 404 not_found). The model must be provided.
            throw new ArgumentException(
                "AI model is required. Set 'aiModel' on the AiConfiguration (recommended) or 'model' on the node.",
                nameof(options.Model));
        }

        return options.Model;
    }

    internal static int ResolveMaxTokens(LlmQueryOptions options,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        // AB#8498: the prompt template's MaxTokens wins over the AiConfiguration.
        if (template?.MaxTokens is { } templateMaxTokens)
        {
            nodeContext.Debug($"Max tokens {templateMaxTokens} loaded from AiPromptTemplate '{template.Name}'");
            return templateMaxTokens;
        }

        // Prefer the max tokens configured on the AiConfiguration entity (maxTokens), mirroring
        // how the model is resolved from it. Falls back to the node's own MaxTokens property
        // (and thus its default) when the AiConfiguration has no usable maxTokens. int.TryParse
        // on JsonNode.ToString() accepts the value both as JSON number and as string.
        var value = ReadAiConfigurationProperty(options.ApiKeyConfigurationName, "maxTokens", etlContext,
            nodeContext);
        if (value != null && int.TryParse(value.ToString(), out var maxTokens) && maxTokens > 0)
        {
            nodeContext.Debug(
                $"Max tokens {maxTokens} loaded from configuration '{options.ApiKeyConfigurationName}'");
            return maxTokens;
        }

        return options.MaxTokens;
    }

    /// <summary>
    ///     The temperature of the run: prompt template, then AiConfiguration, then the node. <c>null</c> when
    ///     none of them sets one; the request then carries none and the model's default applies.
    /// </summary>
    internal static double? ResolveTemperature(LlmQueryOptions options,
        IMeshEtlContext etlContext, INodeContext nodeContext, AiPromptTemplateSettings? template = null)
    {
        // AB#8498: the prompt template's Temperature (already range-checked) wins over the AiConfiguration.
        if (template?.Temperature is { } templateTemperature)
        {
            nodeContext.Debug($"Temperature {templateTemperature} loaded from AiPromptTemplate '{template.Name}'");
            return templateTemperature;
        }

        // Prefer the temperature configured on the AiConfiguration entity (temperature),
        // mirroring the maxTokens/aiModel resolution. 0.0 is a meaningful value, so the only
        // rejection criteria are absence and the API's valid range (0.0 to 1.0). Parsed with
        // the invariant culture; accepts the value both as JSON number and as string.
        var value = ReadAiConfigurationProperty(options.ApiKeyConfigurationName, "temperature", etlContext,
            nodeContext);
        if (value != null &&
            double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var temperature) &&
            temperature is >= 0.0 and <= 1.0)
        {
            nodeContext.Debug(
                $"Temperature {temperature} loaded from configuration '{options.ApiKeyConfigurationName}'");
            return temperature;
        }

        return options.Temperature;
    }

    /// <summary>
    ///     One guarded read for every AiConfiguration lookup: <c>IsDefined</c> only proves the name is
    ///     known, the stored payload can still be empty, malformed or not an object, and that must end in the callers'
    ///     documented fallback (warning, node default) rather than a parse exception. The warning names
    ///     the position of the defect, never a part of the payload: the payload holds the API key.
    /// </summary>
    internal static JsonNode? ReadAiConfigurationProperty(string? configurationName, string property,
        IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(configurationName) ||
            !etlContext.GlobalConfiguration.IsDefined(configurationName))
        {
            return null;
        }

        var rawJson = etlContext.GlobalConfiguration.GetRawJson(configurationName);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(rawJson) is JsonObject payload)
            {
                return payload[property];
            }

            nodeContext.Warning($"AiConfiguration '{configurationName}' is not a JSON object.");
            return null;
        }
        catch (JsonException ex)
        {
            nodeContext.Warning(
                $"AiConfiguration '{configurationName}' is not valid JSON (line {ex.LineNumber}, " +
                $"byte {ex.BytePositionInLine}).");
            return null;
        }
    }

    /// <summary>
    ///     A text value of the AiConfiguration. A value of another type is no usable setting and ends in the
    ///     caller's fallback, like a missing one.
    /// </summary>
    internal static string? ReadAiConfigurationString(string? configurationName, string property,
        IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        return ReadAiConfigurationProperty(configurationName, property, etlContext, nodeContext) is JsonValue value &&
               value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    /// <summary>
    ///     The chat with the model, including the tool rounds: every round is one call of the chat
    ///     client; a round that ends in tool calls runs them on the MCP session and continues with their
    ///     results. Returns the text of the answer.
    /// </summary>
    private async Task<string> ExecuteChatAsync(LlmQueryOptions options, string apiKey, string model, int maxTokens,
        RunSampling sampling, string systemPrompt, string? mcpServerUrl, ChatMessage userMessage,
        List<AITool>? mcpTools, INodeContext nodeContext, AiUsageAccumulator usage,
        List<ChatMessage>? historyMessages = null, ChatResponseFormat? responseFormat = null)
    {
        using var client = chatClientFactory(apiKey);

        var messages = new List<ChatMessage>();

        // Add conversation history before the current message
        if (historyMessages is { Count: > 0 })
        {
            messages.AddRange(historyMessages);
        }

        messages.Add(userMessage);

        var chatOptions = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = maxTokens,
            Instructions = systemPrompt,
            Tools = mcpTools,
            ResponseFormat = responseFormat
        };
        if (sampling.Temperature is { } temperature)
        {
            chatOptions.Temperature = (float)temperature;
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExactTemperatureProperty] = temperature
            };
        }

        // What the model may call is what it was offered. The allowlist of the node must hold for the calls
        // the model asks for, not only for the list it is shown.
        var offeredTools = new HashSet<string>(mcpTools?.Select(t => t.Name) ?? [], StringComparer.Ordinal);

        for (var round = 0; round < (options.MaxToolRounds + 1); round++)
        {
            ChatResponse response;
            try
            {
                response = await SendAsync(client, messages, chatOptions, sampling);
            }
            catch (LlmAnswerRejectedException rejected)
            {
                // The provider answered and billed the call; the client refused the answer.
                usage.Add(rejected.Usage);
                throw;
            }

            usage.Add(response.Usage);

            if (response.FinishReason == ChatFinishReason.ToolCalls)
            {
                if (round >= options.MaxToolRounds)
                {
                    // The budget is spent. Another batch would run tools whose results nobody reads.
                    break;
                }

                // Add assistant message with tool use
                messages.AddRange(response.Messages);

                // Execute each tool call and collect results
                var toolResults = new List<AIContent>();
                foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
                {
                    if (!offeredTools.Contains(call.Name))
                    {
                        nodeContext.Warning(
                            $"The model asked for the tool '{call.Name}', which was not offered to it. The call is refused.");
                        toolResults.Add(
                            new FunctionResultContent(call.CallId, $"Error: the tool '{call.Name}' is not available.")
                            {
                                Exception = new InvalidOperationException("The tool was not offered to the model.")
                            });
                        continue;
                    }

                    nodeContext.Debug($"Executing MCP tool: {DescribeToolCall(call)}");

                    try
                    {
                        var toolResult = await McpSession.CallToolAsync(mcpServerUrl, etlContext.TenantId,
                            call.Name, call.Arguments);

                        toolResults.Add(new FunctionResultContent(call.CallId,
                            CapToolResult(RedactSecrets(toolResult, nodeContext), call.Name,
                                options.MaxToolResultChars, nodeContext)));
                    }
                    catch (Exception ex)
                    {
                        nodeContext.Warning($"MCP tool '{call.Name}' failed: {ex.Message}");
                        toolResults.Add(
                            new FunctionResultContent(call.CallId, RedactSecrets($"Error: {ex.Message}", nodeContext))
                            {
                                Exception = nodeContext.SecretRegistry?.RedactException(ex) ?? ex
                            });
                    }
                }

                // Add tool results as user message
                messages.Add(new ChatMessage(ChatRole.Tool, toolResults));

                nodeContext.Debug($"Tool round {round + 1} completed, continuing conversation");
                continue;
            }

            // A "max_tokens" stop reason means the model hit the output-token limit before it
            // finished — the returned text is a truncated fragment (for JSON responses, typically
            // cut mid-object, so it fails to parse and silently "degrades"). Surface it as a clear,
            // specific error instead of returning the partial text: the pipeline execution then
            // records *why* nothing could be processed (e.g. why no documents were assigned). A
            // pipeline that prefers to skip-and-retry rather than fail can still opt in via
            // ContinueOnError. See AB#4544 (the root-cause class this makes visible).
            if (response.FinishReason == ChatFinishReason.Length)
            {
                throw new InvalidOperationException(LlmResponseProcessor.BuildTruncationMessage(maxTokens));
            }

            // Any other finish reason — the first text part is the answer
            if (FirstText(response) is { } answer)
            {
                return answer;
            }

            throw new InvalidOperationException("No text content in Claude response");
        }

        throw new InvalidOperationException(
            $"Claude exceeded maximum tool rounds ({options.MaxToolRounds})");
    }

    /// <summary>
    ///     One repair call of the jsonSchema mode (AB#8498): a fresh single-turn request with the
    ///     repair system prompt and the repair prompt only — no history, no tools, no original
    ///     content — with the same constrained-decoding schema, at temperature 0 when the run sends a
    ///     temperature and without one when it does not (a model that refused the temperature is not asked
    ///     with one again).
    /// </summary>
    private async Task<string> ExecuteJsonRepairCallAsync(string apiKey, string model, int maxTokens,
        RunSampling sampling, string repairPrompt, ChatResponseFormat responseFormat, AiUsageAccumulator usage)
    {
        using var client = chatClientFactory(apiKey);

        var response = await SendAsync(client, [new ChatMessage(ChatRole.User, repairPrompt)],
            new ChatOptions
            {
                ModelId = model,
                MaxOutputTokens = maxTokens,
                Temperature = sampling.Temperature == null ? null : 0f,
                Instructions = AiJsonSchemaResponseProcessor.RepairSystemPrompt,
                ResponseFormat = responseFormat
            }, sampling);
        usage.Add(response.Usage);

        if (response.FinishReason == ChatFinishReason.Length)
        {
            throw new InvalidOperationException(LlmResponseProcessor.BuildTruncationMessage(maxTokens));
        }

        return FirstText(response) ?? string.Empty;
    }

    /// <summary>
    ///     One call of the chat client. When the provider refuses the temperature, the call is made once more
    ///     without it, with one warning; the run then stays without a temperature (later tool rounds and repair
    ///     calls included). A second refusal is not caught.
    /// </summary>
    private static async Task<ChatResponse> SendAsync(IChatClient client, IList<ChatMessage> messages,
        ChatOptions chatOptions, RunSampling sampling)
    {
        try
        {
            return await client.GetResponseAsync(messages, chatOptions);
        }
        catch (LlmSamplingRejectedException rejection) when (chatOptions.Temperature != null)
        {
            sampling.NodeContext.Warning(
                $"The model refused the temperature; {sampling.NodeDisplayName} asks once more without it. " +
                (sampling.TemperatureIsOptional
                    ? "Remove the temperature from the node, the prompt template or the AiConfiguration to save the " +
                      "extra call."
                    : "Remove it from the prompt template and the AiConfiguration, or move to LlmQuery@1, where the " +
                      "temperature is optional, to save the extra call.") +
                DescribeProviderError(rejection.ProviderError, sampling.NodeContext));
            sampling.Temperature = null;
            chatOptions.Temperature = null;
            chatOptions.AdditionalProperties?.Remove(ExactTemperatureProperty);
            return await client.GetResponseAsync(messages, chatOptions);
        }
    }

    /// <summary>The longest provider error text a warning quotes, in UTF-16 code units, after masking.</summary>
    internal const int MaxQuotedProviderErrorChars = 300;

    /// <summary>
    ///     The provider's words for a refusal, for a log line: masked with the secret registry of the execution
    ///     first, then put on one line and cut (never cut before masking, so no head of a secret is left).
    /// </summary>
    internal static string DescribeProviderError(string? providerError, INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(providerError))
        {
            return string.Empty;
        }

        var masked = RedactSecrets(providerError, nodeContext).ReplaceLineEndings(" ");
        var quoted = masked.Length > MaxQuotedProviderErrorChars
            ? TruncateSurrogateSafe(masked, MaxQuotedProviderErrorChars) + "…"
            : masked;
        return $" Provider answer: {quoted}";
    }

    /// <summary>
    ///     The temperature of one run, cleared when the provider refused it, and what the warning about that
    ///     needs: the node's name and its context.
    /// </summary>
    private sealed class RunSampling(double? temperature, string nodeDisplayName, INodeContext nodeContext,
        bool temperatureIsOptional)
    {
        public double? Temperature { get; set; } = temperature;
        public string NodeDisplayName { get; } = nodeDisplayName;
        public INodeContext NodeContext { get; } = nodeContext;
        public bool TemperatureIsOptional { get; } = temperatureIsOptional;
    }

    /// <summary>The first text part of an answer; further text parts are not part of the result.</summary>
    private static string? FirstText(ChatResponse response)
    {
        return response.Messages.SelectMany(m => m.Contents).OfType<TextContent>().FirstOrDefault()?.Text;
    }

    /// <summary>
    ///     What a tool sent back, before it goes to the model: every registered secret of the execution is
    ///     masked. A tool server that echoes the bearer of its caller would otherwise hand the token to the
    ///     provider and, through the answer of the model, to the data context.
    /// </summary>
    private static string RedactSecrets(string text, INodeContext nodeContext)
    {
        return nodeContext.SecretRegistry?.Redact(text) ?? text;
    }

    /// <summary>
    ///     A tool call for the log: the tool and the names of its arguments. Names only: argument values
    ///     are document text, queries or ids and belong in the tool result, not in the execution log.
    /// </summary>
    internal static string DescribeToolCall(FunctionCallContent call)
    {
        var argumentNames = call.Arguments is { Count: > 0 }
            ? string.Join(", ", call.Arguments.Keys)
            : string.Empty;
        return $"{call.Name}({argumentNames})";
    }

    /// <summary>
    ///     Caps a tool result before it enters the conversation, to prevent exceeding the context window.
    ///     An oversized result is cut at <paramref name="maxChars" /> and marked as truncated so the model
    ///     knows data is missing.
    /// </summary>
    internal static string CapToolResult(string toolResult, string toolName, int maxChars,
        INodeContext nodeContext)
    {
        if (toolResult.Length <= maxChars)
        {
            return toolResult;
        }

        nodeContext.Warning(
            $"MCP tool '{toolName}' result truncated from {toolResult.Length} to {maxChars} chars. " +
            "Use attributePaths parameter to reduce response size.");
        return TruncateSurrogateSafe(toolResult, maxChars) +
               "\n\n[TRUNCATED - result too large. Use attributePaths parameter to request only needed fields.]";
    }

    /// <summary>
    ///     Cuts <paramref name="text" /> to at most <paramref name="maxChars" /> UTF-16 code units without
    ///     splitting a surrogate pair: a lone high surrogate would be replaced by U+FFFD when the tool
    ///     result is serialized for the follow-up request, changing the content the model sees.
    /// </summary>
    internal static string TruncateSurrogateSafe(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
        {
            cut--;
        }

        return text[..cut];
    }
}

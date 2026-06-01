using System.ClientModel;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using OpenAI;
// IMPORTANT: do NOT add `using OpenAI.Chat;` — it conflicts with Microsoft.Extensions.AI
// on ChatMessage / ChatResponseFormat / ChatRole / ChatOptions. Use the fully-qualified
// OpenAI.Chat.ChatClient at the construction site instead.

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(LlmQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class LlmQueryNode(NodeDelegate next, IMeshEtlContext etlContext)
    : IPipelineNode
{
    /// <summary>
    /// Well-known ActivitySource name emitted by this node via
    /// <c>Microsoft.Extensions.AI.OpenTelemetry</c>. Use this constant when
    /// configuring OTel collectors (`AddSource(...)`) or filtering in Grafana
    /// to capture <c>gen_ai.*</c> spans produced by LlmQuery@1 calls.
    /// </summary>
    internal const string ActivitySourceName = "Meshmakers.Octo.Sdk.MeshAdapter.LlmQuery";


    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<LlmQueryNodeConfiguration>();

        // Allows for cancellation via timeout or upstream interrupt.
        using var timeoutCts = new CancellationTokenSource(config.Timeout);
        var ct = timeoutCts.Token;

        try
        {
            // Validate
            if (string.IsNullOrEmpty(config.Question))
            {
                throw new ArgumentException("Question is required", nameof(config.Question));
            }

            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(
                    nodeContext, nameof(config.Path));
            }

            // Resolve API key. May legitimately be null for self-hosted OpenAI-compat
            // backends without authentication (e.g. local Ollama). The OpenAI SDK
            // requires a non-null credential — we fall back to a dummy string.
            var apiKey = ResolveApiKey(config, etlContext, nodeContext);

            nodeContext.Debug(
                $"Starting LlmQuery (provider: {config.Provider}, model: {config.Model})");

            // Read source content
            var mainContent = dataContext.Current?.SelectToken(config.Path)?.ToString();
            if (string.IsNullOrEmpty(mainContent))
            {
                nodeContext.Warning($"No content found at path: {config.Path}");
                await next(dataContext, nodeContext);
                return;
            }

            // Build the context (main content + any additional data paths)
            var context = BuildContext(mainContent, config.DataPaths, dataContext, nodeContext);

            // Build the user prompt (CONTENT / QUESTION / optional JSON sample)
            var userPrompt = BuildUserPrompt(config.Question, context,
                config.ResponseFormat, config.JsonFormatSample);

            // Construct IChatClient based on provider
            var client = ConstructClient(config, apiKey);

            // Build messages list: System, optional history, then current user prompt.
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, config.SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            if (!string.IsNullOrEmpty(config.ConversationHistoryPath))
            {
                var history = LoadHistory(config.ConversationHistoryPath, dataContext, nodeContext);
                if (history.Count > 0)
                {
                    // Insert history between System (index 0) and User (index 1).
                    messages.InsertRange(1, history);
                }
            }

            // Build ChatOptions from config
            var options = new ChatOptions
            {
                ModelId = config.Model,
                MaxOutputTokens = config.MaxTokens,
                Temperature = (float?)config.Temperature,
                TopP = config.TopP,
                TopK = config.TopK,
                ResponseFormat = config.ResponseFormat.Equals(
                    "json", StringComparison.OrdinalIgnoreCase)
                    ? ChatResponseFormat.Json
                    : ChatResponseFormat.Text
            };

            nodeContext.Debug($"Calling LLM with {context.Length} characters of context");

            // Call the LLM via the IChatClient abstraction
            var response = await client.GetResponseAsync(messages, options, ct);
            var aiResponse = response.Text;

            if (string.IsNullOrEmpty(aiResponse))
            {
                throw new InvalidOperationException("Empty response from LLM");
            }

            nodeContext.Info(
                $"Received LLM response with {aiResponse.Length} characters " +
                $"({response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount} tokens)");

            // Parse + post-process the response (mirrors the Anthropic node's pattern,
            // including the prose-wrapped-JSON extraction fallback in ExtractJsonFromText).
            var processedResponse = ProcessResponse(aiResponse, config.ResponseFormat, nodeContext);

            // Store the processed response
            dataContext.SetValueByPath(
                config.TargetPath,
                processedResponse,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode,
                RtNewtonsoftSerializer.DefaultSerializer
            );

            // Optionally also store the raw response
            if (config.IncludeRawResponse)
            {
                dataContext.SetValueByPath(
                    config.RawResponseOutputPath ?? "$.RawAiResponse",
                    aiResponse,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode,
                    RtNewtonsoftSerializer.DefaultSerializer
                );
            }

            nodeContext.Info("LlmQuery completed successfully");
        }
        catch (OperationCanceledException)
        {
            // Don't swallow cancellation. ContinueOnError does not apply to deliberate
            // cancellation — let the pipeline runtime see it and react.
            throw;
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during LlmQuery ({ex.GetType().Name}): {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    // ----------------------------------------------------------------------
    // Provider factory — v0.1 supports OpenAI-compatible only.
    // Anthropic native branch lands in Spike 4.
    // ----------------------------------------------------------------------

    private static IChatClient ConstructClient(LlmQueryNodeConfiguration config, string? apiKey)
    {
        return config.Provider switch
        {
            LlmProvider.OpenAiCompatible => ConstructOpenAiCompatibleClient(config, apiKey),

            // Anthropic enum value is reserved for the native-Claude branch
            // landing in Spike 4 (which will consolidate AnthropicAiQuery@1
            // into this node). Until then, the existing AnthropicAiQuery@1
            // node remains the supported path for native Anthropic features
            // (MCP tool use, native message format, Claude-specific options).
            LlmProvider.Anthropic => throw new NotSupportedException(
                "Provider 'Anthropic' is not available in this build. " +
                "For native Anthropic support today, use the existing " +
                "AnthropicAiQuery@1 node instead. " +
                "This branch will be consolidated into LlmQuery@1 in Spike 4."),

            _ => throw new ArgumentOutOfRangeException(nameof(config.Provider),
                $"Unsupported provider: {config.Provider}")
        };
    }

    private static IChatClient ConstructOpenAiCompatibleClient(
        LlmQueryNodeConfiguration config, string? apiKey)
    {
        // ApiKey may be a real API key (OpenAI cloud, Azure OpenAI, OpenRouter, vLLM
        // with auth) or a dummy string (Ollama, self-hosted without auth — must be
        // non-empty per the OpenAI SDK contract).
        var credential = new ApiKeyCredential(apiKey ?? "unused");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        // Fully-qualified ChatClient avoids the OpenAI.Chat / Microsoft.Extensions.AI
        // namespace ambiguity on ChatMessage / ChatOptions / etc.
        //
        // The .UseOpenTelemetry() decorator emits gen_ai.* spans on the
        // ActivitySourceName above — provider-agnostic telemetry that lets the
        // same Grafana panels work across OpenAI, Anthropic, Ollama, etc.
        // Sensitive data (prompts/responses) is OFF by default; flip via
        // configure callback if a future audit needs it.
        return new OpenAI.Chat.ChatClient(
                model: config.Model,
                credential: credential,
                options: options)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: ActivitySourceName)
            .Build();
    }

    // ----------------------------------------------------------------------
    // ApiKey resolution: prefer ApiKeyConfigurationName (CK entity lookup),
    // fall back to direct ApiKey field. Returns null if neither is set —
    // downstream uses "unused" as a dummy for backends without auth.
    // ----------------------------------------------------------------------

    private static string? ResolveApiKey(
        LlmQueryNodeConfiguration config, IMeshEtlContext etlContext, INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(config.ApiKeyConfigurationName)) return config.ApiKey;
        if (etlContext.GlobalConfiguration.IsDefined(config.ApiKeyConfigurationName))
        {
            var rawJson = etlContext.GlobalConfiguration.GetRawJson(config.ApiKeyConfigurationName);
            var configDoc = JObject.Parse(rawJson);
            var key = configDoc.Value<string>("apiKey");
            if (!string.IsNullOrEmpty(key))
            {
                nodeContext.Debug(
                    $"API key loaded from configuration '{config.ApiKeyConfigurationName}'");
                return key;
            }
        }

        nodeContext.Warning(
            $"AiConfiguration '{config.ApiKeyConfigurationName}' not found or has no ApiKey. " +
            "Falling back to ApiKey field (may be null for backends without auth).");

        return config.ApiKey;
    }

    // ----------------------------------------------------------------------
    // Context construction: assembles main content + optional additional
    // data-path values into a single string for the user prompt.
    // ----------------------------------------------------------------------

    private static string BuildContext(
        string mainContent, string[]? dataPaths, IDataContext dataContext, INodeContext nodeContext)
    {
        var ctx = new StringBuilder();
        ctx.AppendLine("Main Content:");
        ctx.AppendLine(mainContent);
        ctx.AppendLine();

        if (dataPaths is not { Length: > 0 }) return ctx.ToString();
        ctx.AppendLine("Additional Context:");
        foreach (var dataPath in dataPaths)
        {
            try
            {
                var data = dataContext.GetSimpleValueByPath<object>(dataPath);
                if (data == null) continue;
                ctx.AppendLine($"Data from {dataPath}:");
                if (data is string s)
                {
                    ctx.AppendLine(s);
                }
                else
                {
                    var jsonData = JsonSerializer.Serialize(data,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        });
                    ctx.AppendLine(jsonData);
                }

                ctx.AppendLine();
            }
            catch (Exception ex)
            {
                nodeContext.Warning(
                    $"Could not retrieve data from path {dataPath}: {ex.Message}");
            }
        }

        return ctx.ToString();
    }

    // ----------------------------------------------------------------------
    // Conversation history loader: reads role/content pairs from a JSON
    // array at the configured path and converts them to ChatMessages.
    // ----------------------------------------------------------------------

    private static List<ChatMessage> LoadHistory(
        string historyPath, IDataContext dataContext, INodeContext nodeContext)
    {
        var result = new List<ChatMessage>();
        var token = dataContext.Current?.SelectToken(historyPath);
        if (token is not JArray array)
        {
            return result;
        }

        foreach (var entry in array)
        {
            var role = entry["role"]?.ToString();
            var content = entry["content"]?.ToString();
            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(content))
            {
                continue;
            }

            var chatRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            result.Add(new ChatMessage(chatRole, content));
        }

        nodeContext.Debug($"Loaded {result.Count} messages from conversation history");
        return result;
    }

    // ----------------------------------------------------------------------
    // Helpers kept verbatim from AnthropicAiQueryNode for response handling.
    // Provider-agnostic, battle-tested for JSON extraction from mixed-prose
    // responses (relevant for Ollama and other less-strict backends).
    // Do not remove without replacing the equivalent functionality.
    // ----------------------------------------------------------------------

    private static string BuildUserPrompt(string question, string context, string responseFormat,
        string jsonExample)
    {
        var promptBuilder = new StringBuilder();

        promptBuilder.AppendLine("Please analyze the following content and answer the question provided.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("CONTENT:");
        promptBuilder.AppendLine("---");
        promptBuilder.AppendLine(context);
        promptBuilder.AppendLine("---");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("QUESTION:");
        promptBuilder.AppendLine(question);
        promptBuilder.AppendLine();

        if (!responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase)) return promptBuilder.ToString();
        promptBuilder.AppendLine("Please provide your response in valid JSON format. For example:");
        if (string.IsNullOrWhiteSpace(jsonExample))
        {
            promptBuilder.AppendLine("{");
            promptBuilder.AppendLine("  \"transactionDate\": \"2024-01-15\",");
            promptBuilder.AppendLine("  \"companyAddress\": \"123 Main St, City, Country\",");
            promptBuilder.AppendLine("  \"grossTotal\": 1200.00,");
            promptBuilder.AppendLine("  \"netTotal\": 1000.00,");
            promptBuilder.AppendLine("  \"taxAmount\": 200.00");
            promptBuilder.AppendLine("}");
        }
        else
        {
            promptBuilder.AppendLine(jsonExample);
        }

        return promptBuilder.ToString();
    }

    private static object ProcessResponse(string aiResponse, string responseFormat, INodeContext nodeContext)
    {
        if (!responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase)) return aiResponse;
        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(aiResponse);
            return jsonElement;
        }
        catch (JsonException)
        {
            var extractedJson = ExtractJsonFromText(aiResponse);
            if (extractedJson != null)
            {
                try
                {
                    var jsonElement = JToken.Parse(extractedJson);
                    nodeContext.Debug("Successfully extracted JSON from mixed response");
                    return jsonElement;
                }
                catch (JsonException ex)
                {
                    nodeContext.Warning(
                        $"Could not parse extracted JSON: {ex.Message}. Returning as text.");
                }
            }
            else
            {
                nodeContext.Warning("No JSON block found in response. Returning as text.");
            }

            return aiResponse;
        }
    }

    private static string? ExtractJsonFromText(string text)
    {
        var jsonBlockStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonBlockStart >= 0)
        {
            var jsonStart = text.IndexOf('\n', jsonBlockStart) + 1;
            var jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd > jsonStart)
            {
                return text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        var braceStart = text.IndexOf('{');
        if (braceStart < 0) return null;

        var braceCount = 0;
        for (var i = braceStart; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    braceCount++;
                    break;
                case '}':
                    braceCount--;
                    break;
            }

            if (braceCount != 0) continue;
            var jsonCandidate = text.Substring(braceStart, i - braceStart + 1);
            if (jsonCandidate.Contains('"') && jsonCandidate.Contains(':'))
            {
                return jsonCandidate;
            }
        }

        return null;
    }
}

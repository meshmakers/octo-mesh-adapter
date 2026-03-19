using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(AnthropicAiQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class AnthropicAiQueryNode(NodeDelegate next, IMeshEtlContext etlContext, IHttpClientFactory httpClientFactory)
    : IPipelineNode
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<AnthropicAiQueryNodeConfiguration>();

        try
        {
            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(nodeContext,
                    nameof(config.Path));
            }

            if (string.IsNullOrEmpty(config.ApiKey))
            {
                throw new ArgumentException("ApiKey is required", nameof(config.ApiKey));
            }

            if (string.IsNullOrEmpty(config.Question))
            {
                throw new ArgumentException("Question is required", nameof(config.Question));
            }

            nodeContext.Debug("Starting Anthropic AI query");

            // Get the main content from the configured path
            var mainContentToken = dataContext.Current?.SelectToken(config.Path);
            var mainContent = mainContentToken?.ToString();
            if (string.IsNullOrEmpty(mainContent))
            {
                nodeContext.Warning($"No content found at path: {config.Path}");
                await next(dataContext, nodeContext);
                return;
            }

            // Build the context from additional data paths
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("Main Content:");
            contextBuilder.AppendLine(mainContent);
            contextBuilder.AppendLine();

            if (config.DataPaths is { Length: > 0 })
            {
                contextBuilder.AppendLine("Additional Context:");

                foreach (var dataPath in config.DataPaths)
                {
                    try
                    {
                        var additionalData = dataContext.GetSimpleValueByPath<object>(dataPath);
                        if (additionalData != null)
                        {
                            contextBuilder.AppendLine($"Data from {dataPath}:");

                            if (additionalData is string strData)
                            {
                                contextBuilder.AppendLine(strData);
                            }
                            else
                            {
                                var jsonData = JsonSerializer.Serialize(additionalData,
                                    new JsonSerializerOptions
                                    {
                                        WriteIndented = true,
                                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                    });
                                contextBuilder.AppendLine(jsonData);
                            }

                            contextBuilder.AppendLine();
                        }
                    }
                    catch (Exception ex)
                    {
                        nodeContext.Warning($"Could not retrieve data from path {dataPath}: {ex.Message}");
                    }
                }
            }

            var fullContext = contextBuilder.ToString();
            nodeContext.Debug($"Querying Claude with {fullContext.Length} characters of context");

            // Build the prompt
            var userPrompt =
                BuildUserPrompt(config.Question, fullContext, config.ResponseFormat, config.JsonFormatSample);

            // Load MCP tools if configured
            List<JsonElement>? mcpTools = null;
            if (!string.IsNullOrEmpty(config.McpServerUrl))
            {
                mcpTools = await LoadMcpToolsAsync(config.McpServerUrl, etlContext.TenantId, nodeContext);
                nodeContext.Info($"Loaded {mcpTools?.Count ?? 0} MCP tools from OctoMesh");
            }

            // Load conversation history if configured
            List<object>? historyMessages = null;
            if (!string.IsNullOrEmpty(config.ConversationHistoryPath))
            {
                var historyToken = dataContext.Current?.SelectToken(config.ConversationHistoryPath);
                if (historyToken is Newtonsoft.Json.Linq.JArray historyArray)
                {
                    historyMessages = new List<object>();
                    foreach (var entry in historyArray)
                    {
                        var role = entry["role"]?.ToString();
                        var content = entry["content"]?.ToString();
                        if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(content))
                        {
                            historyMessages.Add(new { role, content });
                        }
                    }

                    nodeContext.Debug($"Loaded {historyMessages.Count} messages from conversation history");
                }
            }

            // Execute Claude API call (with optional tool use loop)
            var aiResponse = await ExecuteClaudeApiAsync(config, userPrompt, mcpTools, nodeContext, historyMessages);

            if (string.IsNullOrEmpty(aiResponse))
            {
                throw new InvalidOperationException("Empty response from Claude");
            }

            nodeContext.Info($"Received AI response with {aiResponse.Length} characters");

            // Process the response based on format
            object processedResponse = ProcessResponse(aiResponse, config.ResponseFormat, nodeContext);

            // Store the processed response
            dataContext.SetValueByPath(
                config.TargetPath,
                processedResponse,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode,
                RtNewtonsoftSerializer.DefaultSerializer
            );

            // Store raw response if requested
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

            nodeContext.Info("AI query completed successfully");
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during AI query: {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    private async Task<string> ExecuteClaudeApiAsync(AnthropicAiQueryNodeConfiguration config,
        string userPrompt, List<JsonElement>? mcpTools, INodeContext nodeContext,
        List<object>? historyMessages = null)
    {
        using var client = httpClientFactory.CreateClient();

        var messages = new List<object>();

        // Add conversation history before the current message
        if (historyMessages is { Count: > 0 })
        {
            messages.AddRange(historyMessages);
        }

        messages.Add(new { role = "user", content = userPrompt });

        for (var round = 0; round < (config.MaxToolRounds + 1); round++)
        {
            // Build request
            var requestObj = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["max_tokens"] = config.MaxTokens,
                ["temperature"] = config.Temperature,
                ["system"] = config.SystemPrompt,
                ["messages"] = messages
            };

            // Add tools if MCP is configured
            if (mcpTools is { Count: > 0 })
            {
                requestObj["tools"] = mcpTools;
            }

            var jsonRequest = JsonSerializer.Serialize(requestObj, JsonOptions);
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Anthropic API error ({response.StatusCode}): {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Check stop reason
            var stopReason = apiResponse.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

            if (stopReason == "tool_use")
            {
                // Extract tool use blocks and execute them
                var contentArray = apiResponse.GetProperty("content");
                var assistantContent = contentArray.Clone();

                // Add assistant message with tool use
                messages.Add(new { role = "assistant", content = assistantContent });

                // Execute each tool call and collect results
                var toolResults = new List<object>();
                foreach (var block in contentArray.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
                    {
                        var toolId = block.GetProperty("id").GetString()!;
                        var toolName = block.GetProperty("name").GetString()!;
                        var toolInput = block.GetProperty("input");

                        nodeContext.Debug($"Executing MCP tool: {toolName}");

                        try
                        {
                            var toolResult =
                                await ExecuteMcpToolAsync(config.McpServerUrl!, etlContext.TenantId,
                                    toolName, toolInput);

                            toolResults.Add(new
                            {
                                type = "tool_result",
                                tool_use_id = toolId,
                                content = toolResult
                            });
                        }
                        catch (Exception ex)
                        {
                            nodeContext.Warning($"MCP tool '{toolName}' failed: {ex.Message}");
                            toolResults.Add(new
                            {
                                type = "tool_result",
                                tool_use_id = toolId,
                                is_error = true,
                                content = $"Error: {ex.Message}"
                            });
                        }
                    }
                }

                // Add tool results as user message
                messages.Add(new { role = "user", content = toolResults });

                nodeContext.Debug($"Tool round {round + 1} completed, continuing conversation");
                continue;
            }

            // stop_reason == "end_turn" or other — extract text response
            if (apiResponse.TryGetProperty("content", out var finalContent) &&
                finalContent.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in finalContent.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        block.TryGetProperty("text", out var textEl))
                    {
                        return textEl.GetString() ?? "";
                    }
                }
            }

            throw new InvalidOperationException("No text content in Claude response");
        }

        throw new InvalidOperationException(
            $"Claude exceeded maximum tool rounds ({config.MaxToolRounds})");
    }

    private async Task<List<JsonElement>?> LoadMcpToolsAsync(string mcpServerUrl, string tenantId,
        INodeContext nodeContext)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            // MCP tools/list via JSON-RPC
            var rpcRequest = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list"
            };

            var url = $"{mcpServerUrl}/{tenantId}/mcp";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(rpcRequest), Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await client.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            nodeContext.Debug($"MCP tools/list response status: {response.StatusCode}, length: {responseText.Length}, content-type: {response.Content.Headers.ContentType}");
            if (responseText.Length < 500)
            {
                nodeContext.Debug($"MCP tools/list response body: {responseText}");
            }

            // Parse SSE response — look for JSON-RPC result in the event stream
            var jsonRpcResponse = ExtractJsonRpcFromSse(responseText);
            if (jsonRpcResponse == null)
            {
                nodeContext.Warning($"Could not parse MCP tools/list response. Raw: {responseText[..Math.Min(200, responseText.Length)]}");
                return null;
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(jsonRpcResponse);
            if (!doc.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools))
            {
                return null;
            }

            // Convert MCP tool format to Anthropic tool format
            var anthropicTools = new List<JsonElement>();
            foreach (var tool in tools.EnumerateArray())
            {
                var anthropicTool = new Dictionary<string, object>
                {
                    ["name"] = tool.GetProperty("name").GetString()!,
                    ["description"] = tool.TryGetProperty("description", out var desc)
                        ? desc.GetString() ?? ""
                        : ""
                };

                if (tool.TryGetProperty("inputSchema", out var schema))
                {
                    anthropicTool["input_schema"] = schema;
                }
                else
                {
                    anthropicTool["input_schema"] = new { type = "object", properties = new { } };
                }

                var serialized = JsonSerializer.Serialize(anthropicTool, JsonOptions);
                anthropicTools.Add(JsonSerializer.Deserialize<JsonElement>(serialized));
            }

            return anthropicTools;
        }
        catch (Exception ex)
        {
            nodeContext.Warning($"Failed to load MCP tools: {ex.Message}");
            return null;
        }
    }

    private async Task<string> ExecuteMcpToolAsync(string mcpServerUrl, string tenantId, string toolName,
        JsonElement toolInput)
    {
        using var client = httpClientFactory.CreateClient();

        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = toolInput
            }
        };

        var url = $"{mcpServerUrl}/{tenantId}/mcp";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(rpcRequest, JsonOptions), Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        var jsonRpcResponse = ExtractJsonRpcFromSse(responseText);
        if (jsonRpcResponse == null)
        {
            throw new InvalidOperationException($"No valid response from MCP tool '{toolName}'");
        }

        var doc = JsonSerializer.Deserialize<JsonElement>(jsonRpcResponse);

        if (doc.TryGetProperty("error", out var error))
        {
            var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
            throw new InvalidOperationException($"MCP tool error: {errorMsg}");
        }

        if (doc.TryGetProperty("result", out var resultProp))
        {
            // MCP tool result contains "content" array with text items
            if (resultProp.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                    {
                        sb.AppendLine(text.GetString());
                    }
                }

                return sb.ToString();
            }

            return resultProp.ToString();
        }

        return responseText;
    }

    private static string? ExtractJsonRpcFromSse(string responseText)
    {
        // SSE format: "event: message\ndata: {json}\n\n"
        // Or direct JSON-RPC response
        if (responseText.TrimStart().StartsWith('{'))
        {
            return responseText;
        }

        foreach (var line in responseText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:"))
            {
                var jsonPart = trimmed["data:".Length..].Trim();
                if (jsonPart.StartsWith('{'))
                {
                    return jsonPart;
                }
            }
        }

        return null;
    }

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

        if (responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
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
        }

        return promptBuilder.ToString();
    }

    private static object ProcessResponse(string aiResponse, string responseFormat, INodeContext nodeContext)
    {
        if (responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
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

        return aiResponse;
    }

    private static string? ExtractJsonFromText(string text)
    {
        var jsonBlockStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonBlockStart >= 0)
        {
            var jsonStart = text.IndexOf('\n', jsonBlockStart) + 1;
            var jsonEnd = text.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        var braceStart = text.IndexOf('{');
        if (braceStart >= 0)
        {
            var braceCount = 0;
            var jsonStart = braceStart;

            for (var i = braceStart; i < text.Length; i++)
            {
                if (text[i] == '{')
                    braceCount++;
                else if (text[i] == '}')
                    braceCount--;

                if (braceCount == 0)
                {
                    var jsonCandidate = text.Substring(jsonStart, i - jsonStart + 1);
                    if (jsonCandidate.Contains("\"") && jsonCandidate.Contains(":"))
                    {
                        return jsonCandidate;
                    }
                }
            }
        }

        return null;
    }
}

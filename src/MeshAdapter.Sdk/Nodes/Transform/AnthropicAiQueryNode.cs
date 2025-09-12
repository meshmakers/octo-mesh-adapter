using System.Text;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(AnthropicAiQueryNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class AnthropicAiQueryNode(NodeDelegate next) : IPipelineNode
{
    private static readonly HttpClient HttpClient = new();

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<AnthropicAiQueryNodeConfiguration>();
        
        try
        {
            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(nodeContext, nameof(config.Path));
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
            var mainContent = dataContext.GetSimpleValueByPath<string>(config.Path);
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
                                // Serialize complex objects to JSON
                                var jsonData = JsonSerializer.Serialize(additionalData, new JsonSerializerOptions 
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
            var userPrompt = BuildUserPrompt(config.Question, fullContext, config.ResponseFormat);
            
            // Create the API request
            var requestBody = new
            {
                model = config.Model,
                max_tokens = config.MaxTokens,
                temperature = config.Temperature,
                system = config.SystemPrompt,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt
                    }
                }
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            // Set headers
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = content
            };
            
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            // Make the API call
            using var response = await HttpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Anthropic API error ({response.StatusCode}): {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Extract the text content
            if (!apiResponse.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("No content array found in Claude response");
            }

            var firstContent = contentArray.EnumerateArray().FirstOrDefault();
            if (!firstContent.TryGetProperty("text", out var textElement))
            {
                throw new InvalidOperationException("No text found in Claude response");
            }

            var aiResponse = textElement.GetString();
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

    private static string BuildUserPrompt(string question, string context, string responseFormat)
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
            promptBuilder.AppendLine("{");
            promptBuilder.AppendLine("  \"transactionDate\": \"2024-01-15\",");
            promptBuilder.AppendLine("  \"companyAddress\": \"123 Main St, City, Country\",");
            promptBuilder.AppendLine("  \"grossTotal\": 1200.00,");
            promptBuilder.AppendLine("  \"netTotal\": 1000.00,");
            promptBuilder.AppendLine("  \"taxAmount\": 200.00");
            promptBuilder.AppendLine("}");
        }

        return promptBuilder.ToString();
    }

    private static object ProcessResponse(string aiResponse, string responseFormat, INodeContext nodeContext)
    {
        if (responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Try to parse the entire response as JSON first
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(aiResponse);
                return jsonElement;
            }
            catch (JsonException)
            {
                // If that fails, try to extract JSON from text that contains both explanation and JSON
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
                        nodeContext.Warning($"Could not parse extracted JSON: {ex.Message}. Returning as text.");
                    }
                }
                else
                {
                    nodeContext.Warning("No JSON block found in response. Returning as text.");
                }
                
                return aiResponse;
            }
        }

        // Return as text for other formats
        return aiResponse;
    }

    private static string? ExtractJsonFromText(string text)
    {
        // Look for JSON blocks marked with ```json or just { }
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

        // Look for JSON objects starting with { and ending with }
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
                    // Quick validation that this looks like JSON
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
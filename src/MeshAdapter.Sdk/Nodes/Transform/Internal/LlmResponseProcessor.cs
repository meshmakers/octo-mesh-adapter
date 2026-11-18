using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     What an LLM query does with the model's answer when no JSON Schema is set: refuse simulated tool
///     calls, name a truncated answer, and turn a JSON answer into a value the data context can carry.
///     Moved unchanged out of <c>AnthropicAiQueryNode</c> so every LLM query node treats an answer the
///     same way.
/// </summary>
internal static class LlmResponseProcessor
{
    /// <summary>Thrown when the model answered with tool-call markup as text (see <see cref="ContainsSimulatedToolCalls" />).</summary>
    internal const string SimulatedToolCallsMessage =
        "The AI answered with simulated tool calls (tool-call markup in the text) — its results are " +
        "invented, not queried. The answer was refused. Check that the MCP server URL " +
        "(AiConfiguration.McpServerUrl) is configured and reachable so real tools are available.";

    /// <summary>
    ///     Does a TEXT answer carry tool-call markup? Real tool use never does — it arrives as
    ///     <c>tool_use</c> content blocks — so any of these tags in the text means the model
    ///     imitated a tool and made its result up (observed 5.10.2026: <c>&lt;tool_call&gt;</c> +
    ///     <c>&lt;tool_response&gt;</c> with invented invoices, on a tenant without an MCP URL).
    /// </summary>
    internal static bool ContainsSimulatedToolCalls(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var tag in new[] { "<tool_call>", "<tool_response>", "<function_calls>", "<tool_use>" })
        {
            if (text.Contains(tag, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the message thrown when the model stops with <c>stop_reason: "max_tokens"</c>. The
    /// answer was truncated at the output-token cap, so the (typically JSON) result is incomplete
    /// and must not be processed as-is. The message names the cause and the two fixes (raise
    /// maxTokens or shrink the input) so it reads clearly in the pipeline-execution audit trail.
    /// </summary>
    internal static string BuildTruncationMessage(int maxTokens)
    {
        return "AI response truncated — the model reached the max_tokens output limit " +
               $"({maxTokens}) before completing its answer, so the result is incomplete and was " +
               "not processed. Increase maxTokens (on the AiConfiguration entity or the node) or " +
               "reduce the input size (e.g. a smaller batch or less context).";
    }

    /// <summary>
    /// Turns the model's raw answer into the value written to the data context: the parsed JSON for
    /// <c>responseFormat: json</c> (recovering a JSON value embedded in prose when the whole answer
    /// does not parse), the answer text otherwise. Every JSON result passes through
    /// <see cref="SanitizeModelJson" /> — see there for why a duplicate key must be removed rather
    /// than caught.
    /// </summary>
    internal static object ProcessResponse(string aiResponse, string responseFormat, INodeContext nodeContext)
    {
        if (responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(aiResponse);
                return SanitizeModelJson(jsonElement, nodeContext);
            }
            catch (JsonException)
            {
                var extractedJson = ExtractJsonFromText(aiResponse);
                if (extractedJson != null)
                {
                    try
                    {
                        // JsonDocument, not JsonNode.Parse: the node representation would accept a
                        // duplicate key here and only throw later, deep in a downstream node (see
                        // SanitizeModelJson). The element reader lets the sanitizer see it first.
                        using var extractedDocument = JsonDocument.Parse(extractedJson);
                        nodeContext.Debug("Successfully extracted JSON from mixed response");
                        return SanitizeModelJson(extractedDocument.RootElement, nodeContext);
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

    /// <summary>
    /// Returns a data-context-safe copy of a model JSON answer: unchanged (detached) when every
    /// object key is unique, and rebuilt with the LAST value of every repeated key otherwise.
    /// <para>
    /// AB#5348: a language model occasionally emits the same key twice in one object — on prod-1
    /// roughly one in five invoice extractions repeated <c>addressComplete</c>, whose rule the
    /// prompt states twice per party. That parses fine here (<see cref="JsonElement" /> keeps both
    /// occurrences), and it is still fine when the value is written to the data context. It
    /// detonates later: the data context hands the value on as a <see cref="JsonObject" />, whose
    /// dictionary is built lazily, so the first downstream node that navigates into the offending
    /// object dies with <c>An item with the same key has already been added</c>. On the accounting
    /// document-analysis flow that is <c>CreateUpdateInfo@1</c> reading
    /// <c>$.ai.result.invoices[0].vendor.addressComplete</c> — which is why this node's
    /// <c>continueOnError</c> never helped: the throw happens after this node has already returned,
    /// outside its try/catch, and takes the whole ForEach iteration with it. A borderline answer is
    /// an expected input from a language model, so it is normalised at the boundary where it enters
    /// the pipeline instead of being caught somewhere it cannot be caught.
    /// </para>
    /// </summary>
    private static object SanitizeModelJson(JsonElement element, INodeContext nodeContext)
    {
        if (!JsonDuplicateKeys.TryFindDuplicateKey(element, out var duplicateKey))
        {
            // Clone detaches from the backing document, which the caller may dispose.
            return element.Clone();
        }

        nodeContext.Warning(
            $"The AI response repeats the JSON key '{duplicateKey}' in one object. Keeping the last " +
            "value of every repeated key — an unrepaired response would fail a later node with " +
            "'An item with the same key has already been added'.");

        return JsonDuplicateKeys.RewriteLastValueWins(element)
               ?? (object)element.Clone();
    }

    /// <summary>
    /// Extracts a JSON value embedded in a mixed prose/markdown response. Handles a fenced
    /// <c>```json … ```</c> block first, otherwise takes the first top-level JSON value — array
    /// <c>[ … ]</c> or object <c>{ … }</c>, whichever appears first — via string/escape-aware brace
    /// matching. The array case matters for this node: the prompt asks for a JSON array of mappings,
    /// and when the model wraps it in prose ("Here are the mappings: [ … ]") the direct
    /// deserialize fails and the fallback must return the whole array, not the first inner object.
    /// </summary>
    internal static string? ExtractJsonFromText(string text)
    {
        var fenced = ExtractFencedJson(text);
        if (fenced != null)
        {
            return fenced;
        }

        var arrayStart = text.IndexOf('[');
        var objectStart = text.IndexOf('{');

        if (arrayStart >= 0 && (objectStart < 0 || arrayStart < objectStart))
        {
            return ExtractBalanced(text, arrayStart, '[', ']');
        }

        if (objectStart >= 0)
        {
            return ExtractBalanced(text, objectStart, '{', '}');
        }

        return null;
    }

    private static string? ExtractFencedJson(string text)
    {
        var fenceStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0)
        {
            return null;
        }

        var jsonStart = text.IndexOf('\n', fenceStart);
        if (jsonStart < 0)
        {
            return null;
        }

        jsonStart++;
        var jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
        return jsonEnd > jsonStart ? text.Substring(jsonStart, jsonEnd - jsonStart).Trim() : null;
    }

    /// <summary>
    /// Returns the balanced <paramref name="open" />…<paramref name="close" /> span starting at
    /// <paramref name="start" />, ignoring brackets inside JSON string literals (honouring
    /// backslash escapes) so a bracket in a value like a reason text does not unbalance the scan.
    /// </summary>
    private static string? ExtractBalanced(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }
}

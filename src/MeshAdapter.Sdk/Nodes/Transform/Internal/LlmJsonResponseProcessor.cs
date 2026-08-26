using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
/// Parses LLM responses for JSON mode. Escalation ladder: strict parse -> extraction
/// from mixed prose -> mechanical repair (free) -> bounded LLM repair -> text fallback.
/// </summary>
internal static class LlmJsonResponseProcessor
{
    /// <summary>
    /// Parses a JSON response. On failure, runs a bounded LLM repair (hard-capped at 2
    /// attempts) that resends ONLY the broken output plus the parser error — never the
    /// original context. Returns the parsed JsonElement, or the original text when repair
    /// is disabled/exhausted.
    /// </summary>
    internal static async Task<object> ProcessWithRepairAsync(
        string aiResponse, LlmQueryNodeConfiguration config, IChatClient client,
        ChatOptions options, INodeContext nodeContext, CancellationToken ct)
    {
        if (!config.ResponseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return aiResponse;
        }

        if (TryParse(aiResponse, nodeContext, out var element, out var parseError))
        {
            return element;
        }

        var maxAttempts = Math.Clamp(config.MaxJsonRepairAttempts, 0, 2);
        var current = aiResponse;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            nodeContext.Warning(
                $"Response is not valid JSON ({parseError}); bounded repair attempt " +
                $"{attempt}/{maxAttempts} (sending only the broken output back, not the original context)");
            nodeContext.Debug($"Unparseable response excerpt: {Truncate(current, 500)}");

            var repairMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You repair invalid JSON. Return ONLY the corrected JSON document - " +
                    "no prose, no code fences. Preserve the content exactly; fix only the " +
                    "syntax (escaping, delimiters, brackets)."),
                new(ChatRole.User,
                    $"The following JSON is invalid. Parser error: {parseError}\n\n{current}")
            };

            var repairOptions = new ChatOptions
            {
                ModelId = options.ModelId,
                MaxOutputTokens = options.MaxOutputTokens,
                Temperature = 0f,
                ResponseFormat = options.ResponseFormat ?? ChatResponseFormat.Json
            };

            var repairResponse = await client.GetResponseAsync(repairMessages, repairOptions, ct);
            current = repairResponse.Text ?? string.Empty;

            if (TryParse(current, nodeContext, out element, out parseError))
            {
                nodeContext.Info($"JSON repaired successfully on attempt {attempt}");
                return element;
            }
        }

        nodeContext.Warning(maxAttempts > 0
            ? $"JSON could not be repaired after {maxAttempts} attempt(s) ({parseError}). Returning as text."
            : $"Could not parse JSON and repair is disabled ({parseError}). Returning as text.");
        return aiResponse;
    }

    /// <summary>
    /// Strict parse, then extraction from mixed prose, then mechanical repair.
    /// </summary>
    private static bool TryParse(string text, INodeContext nodeContext,
        out JsonElement element, out string? error)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(text);
            error = null;
            return true;
        }
        catch (JsonException outerEx)
        {
            var extractedJson = ExtractJsonFromText(text);
            var candidate = extractedJson ?? text;
            if (extractedJson != null)
            {
                try
                {
                    element = JsonSerializer.Deserialize<JsonElement>(extractedJson);
                    nodeContext.Debug("Successfully extracted JSON from mixed response");
                    error = null;
                    return true;
                }
                catch (JsonException innerEx)
                {
                    outerEx = innerEx;
                }
            }

            var repaired = RepairMechanically(candidate);
            if (repaired != candidate)
            {
                try
                {
                    element = JsonSerializer.Deserialize<JsonElement>(repaired);
                    nodeContext.Info("JSON repaired deterministically (no LLM call)");
                    error = null;
                    return true;
                }
                catch (JsonException)
                {
                    // fall through to the original error
                }
            }

            element = default;
            error = outerEx.Message;
            return false;
        }
    }

    /// <summary>
    /// Mechanical repair for the defect classes LLMs actually produce: naked double
    /// quotes inside string values, trailing commas, and unterminated strings/brackets.
    /// Purely syntactic; a wrong quote guess is caught by downstream verification.
    /// </summary>
    internal static string RepairMechanically(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 16);
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '\\':
                        escaped = true;
                        sb.Append(c);
                        continue;
                    case '"':
                    {
                        // Terminator or content? Content when the next non-whitespace char
                        // is not valid JSON continuation.
                        var j = i + 1;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        var isTerminator = j >= text.Length ||
                                           text[j] is ',' or ':' or '}' or ']';
                        if (isTerminator)
                        {
                            inString = false;
                            sb.Append(c);
                        }
                        else
                        {
                            sb.Append("\\\"");
                        }

                        continue;
                    }
                    default:
                        sb.Append(c);
                        continue;
                }
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    sb.Append(c);
                    break;
                case '{':
                    stack.Push('}');
                    sb.Append(c);
                    break;
                case '[':
                    stack.Push(']');
                    sb.Append(c);
                    break;
                case '}' or ']':
                    if (stack.Count > 0 && stack.Peek() == c) stack.Pop();
                    TrimTrailingComma(sb);
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Close what a truncated response left open.
        if (inString) sb.Append('"');
        while (stack.Count > 0)
        {
            TrimTrailingComma(sb);
            sb.Append(stack.Pop());
        }

        return sb.ToString();

        static void TrimTrailingComma(System.Text.StringBuilder builder)
        {
            var k = builder.Length - 1;
            while (k >= 0 && char.IsWhiteSpace(builder[k])) k--;
            if (k >= 0 && builder[k] == ',') builder.Remove(k, 1);
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

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}

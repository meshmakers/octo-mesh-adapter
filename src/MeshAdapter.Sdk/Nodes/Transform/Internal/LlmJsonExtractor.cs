namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
/// Recovers a JSON value from a mixed prose/markdown model response. One implementation for
/// <c>AnthropicAiQuery@1</c> and <c>LlmQuery@1</c>: the node-local copies had drifted (one lacked
/// array support and string/escape awareness), so a prose-wrapped array or a <c>}</c> inside a
/// string value silently turned a JSON pipeline into a string write.
/// </summary>
internal static class LlmJsonExtractor
{
    private const string JsonFence = "```json";

    /// <summary>
    /// A fenced <c>```json … ```</c> block first (single-line or multi-line), otherwise the first
    /// top-level JSON value — array or object, whichever appears first — via string/escape-aware
    /// bracket matching. Returns null when no candidate exists; callers then fall back to the raw text.
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

    /// <summary>
    /// The content between the opening <c>```json</c> fence and the next <c>```</c>, trimmed. Works for
    /// <c>```json {"a":1} ```</c> on one line as well as the usual multi-line block; without a closing
    /// fence the caller's bracket matching takes over.
    /// </summary>
    internal static string? ExtractFencedJson(string text)
    {
        var fenceStart = text.IndexOf(JsonFence, StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0)
        {
            return null;
        }

        var jsonStart = fenceStart + JsonFence.Length;
        var jsonEnd = text.IndexOf("```", jsonStart, StringComparison.Ordinal);
        if (jsonEnd <= jsonStart)
        {
            return null;
        }

        var candidate = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
        return candidate.Length > 0 ? candidate : null;
    }

    /// <summary>
    /// Returns the balanced <paramref name="open" />…<paramref name="close" /> span starting at
    /// <paramref name="start" />, ignoring brackets inside JSON string literals (honouring
    /// backslash escapes) so a bracket in a value like a reason text does not unbalance the scan.
    /// </summary>
    internal static string? ExtractBalanced(string text, int start, char open, char close)
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

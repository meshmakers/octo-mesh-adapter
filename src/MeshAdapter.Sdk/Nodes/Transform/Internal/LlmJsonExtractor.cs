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
    /// A fenced <c>```json … ```</c> block first, otherwise the balanced top-level JSON values in
    /// the text, in order of appearance. The first candidate that parses is returned; when none
    /// parses, the first candidate is returned so the caller's repair tier can work on it. Returns
    /// null when no candidate exists; callers then fall back to the raw text.
    /// </summary>
    internal static string? ExtractJsonFromText(string text)
    {
        string? firstCandidate = null;

        var fenced = ExtractFencedJson(text);
        if (fenced != null)
        {
            if (IsValidJson(fenced))
            {
                return fenced;
            }

            firstCandidate = fenced;
        }

        // Prose such as "Result [draft]: {...}" yields a balanced but meaningless "[draft]" before
        // the real object, so every candidate is tried, not only the first.
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '[' && c != '{')
            {
                continue;
            }

            var candidate = c == '['
                ? ExtractBalanced(text, i, '[', ']')
                : ExtractBalanced(text, i, '{', '}');
            if (candidate == null)
            {
                continue;
            }

            if (IsValidJson(candidate))
            {
                return candidate;
            }

            firstCandidate ??= candidate;
            // Skip past this candidate; nested brackets inside it were already covered.
            i += candidate.Length - 1;
        }

        return firstCandidate;
    }

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(candidate);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
/// Assembles the prompt for LlmQuery@1: context from data paths, user prompt,
/// conversation history, and quote sanitizing for JSON mode.
/// </summary>
internal static class LlmPromptBuilder
{
    /// <summary>
    /// Resolves the optional main content: string values verbatim, other explicit
    /// (non-"$") values as indented JSON. The default "$" root or an absent path
    /// yields null — MCP-only pipelines carry no document payload.
    /// </summary>
    internal static string? ResolveMainContent(IDataContext dataContext, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var kind = dataContext.GetKind(path);
        if (kind == DataKind.String)
        {
            return dataContext.Get<string>(path);
        }

        if (kind != DataKind.Undefined && path != "$")
        {
            return JsonSerializer.Serialize(dataContext.Get<object?>(path),
                new JsonSerializerOptions { WriteIndented = true });
        }

        return null;
    }

    /// <summary>
    /// Builds the context block (main content + optional data-path values). With
    /// <paramref name="sanitizeQuotesInValues"/>, the double-quote glyph family in content
    /// is replaced so the model cannot copy a JSON string terminator into its output.
    /// </summary>
    internal static string BuildContext(
        string? mainContent, string[]? dataPaths, IDataContext dataContext, INodeContext nodeContext,
        bool sanitizeQuotesInValues = false)
    {
        var ctx = new StringBuilder();
        if (!string.IsNullOrEmpty(mainContent))
        {
            ctx.AppendLine("Main Content:");
            ctx.AppendLine(sanitizeQuotesInValues ? SanitizeQuotes(mainContent) : mainContent);
            ctx.AppendLine();
        }

        if (dataPaths is not { Length: > 0 }) return ctx.ToString();
        ctx.AppendLine("Additional Context:");
        foreach (var dataPath in dataPaths)
        {
            try
            {
                var kind = dataContext.GetKind(dataPath);
                if (kind is DataKind.Undefined) continue;

                ctx.AppendLine($"Data from {dataPath}:");
                if (kind == DataKind.String)
                {
                    var s = dataContext.Get<string>(dataPath);
                    ctx.AppendLine(sanitizeQuotesInValues && s != null ? SanitizeQuotes(s) : s);
                }
                else
                {
                    var value = dataContext.Get<object?>(dataPath);
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    if (sanitizeQuotesInValues)
                    {
                        // Sanitize string values only; the serialized JSON syntax stays intact.
                        var node = JsonSerializer.SerializeToNode(value, options);
                        ctx.AppendLine(SanitizeStringValues(node)?.ToJsonString(options));
                    }
                    else
                    {
                        ctx.AppendLine(JsonSerializer.Serialize(value, options));
                    }
                }

                ctx.AppendLine();
            }
            catch (Exception ex)
            {
                nodeContext.Warning($"Could not retrieve data from path {dataPath}: {ex.Message}");
            }
        }

        return ctx.ToString();
    }

    /// <summary>
    /// Builds the user prompt (CONTENT / QUESTION / optional JSON example).
    /// </summary>
    internal static string BuildUserPrompt(string question, string context, string responseFormat,
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

        if (!responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return promptBuilder.ToString();
        }

        if (string.IsNullOrWhiteSpace(jsonExample))
        {
            promptBuilder.AppendLine("Please provide your response in valid JSON format.");
        }
        else
        {
            promptBuilder.AppendLine("Please provide your response in valid JSON format. For example:");
            promptBuilder.AppendLine(jsonExample);
        }

        return promptBuilder.ToString();
    }

    /// <summary>
    /// Loads conversation history ({role, content} entries) from the configured path.
    /// Content may be a plain string or an OpenAI content-parts array.
    /// </summary>
    internal static List<ChatMessage> LoadHistory(
        string historyPath, IDataContext dataContext, INodeContext nodeContext)
    {
        var result = new List<ChatMessage>();
        if (dataContext.GetKind(historyPath) != DataKind.Array)
        {
            return result;
        }

        foreach (var entry in dataContext.SelectMatches($"{historyPath}[*]"))
        {
            var role = entry.GetKind("$.role") is DataKind.String
                ? entry.Get<string>("$.role")
                : null;
            var content = entry.GetKind("$.content") switch
            {
                DataKind.String => entry.Get<string>("$.content"),
                DataKind.Array => ExtractTextFromContentParts(entry),
                _ => null
            };
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

    /// <summary>
    /// Replaces the double-quote glyph family (" „ “ ” « ») with a single quote.
    /// Models normalize typographic quotes to straight ones when quoting verbatim,
    /// which terminates the JSON string under constrained decoding.
    /// </summary>
    internal static string SanitizeQuotes(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c is '"' or '„' or '“' or '”' or '«' or '»' ? '\'' : c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rebuilds a JSON tree with <see cref="SanitizeQuotes"/> applied to every string
    /// value; keys and structure are untouched.
    /// </summary>
    internal static JsonNode? SanitizeStringValues(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var kvp in obj)
                {
                    result[kvp.Key] = SanitizeStringValues(kvp.Value);
                }

                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    result.Add(SanitizeStringValues(item));
                }

                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var s):
                return JsonValue.Create(SanitizeQuotes(s));
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Flattens an OpenAI content-parts array into a single string (text parts only).
    /// </summary>
    private static string? ExtractTextFromContentParts(IDataContext entry)
    {
        var sb = new StringBuilder();
        foreach (var part in entry.SelectMatches("$.content[*]"))
        {
            if (part.GetKind("$.type") is not DataKind.String ||
                !string.Equals(part.Get<string>("$.type"), "text", StringComparison.OrdinalIgnoreCase) ||
                part.GetKind("$.text") is not DataKind.String)
            {
                continue;
            }

            var text = part.Get<string>("$.text");
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}

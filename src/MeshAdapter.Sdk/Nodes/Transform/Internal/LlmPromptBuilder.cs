using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.AI;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The texts an LLM query sends: question, system prompt, user prompt and conversation history.
///     Moved unchanged out of <c>AnthropicAiQueryNode</c> so every LLM query node builds the same prompt;
///     the history reader is lifted from the LlmQuery prototype
///     (<c>philipp/AB#4861_llmquery-guaranteed-json</c>, Philipp Schwaab).
/// </summary>
internal static class LlmPromptBuilder
{
    // Indented rendering for non-string DataPaths values in the AI prompt context (mirrors the
    // former JsonNode.ToJsonString(WriteIndented:true)). Prompt text only — not a wire format.
    internal static readonly JsonSerializerOptions IndentedPromptOptions = new() { WriteIndented = true };

    /// <summary>
    ///     The question of this run (AB#8498): the inline <c>question</c> wins, otherwise the
    ///     template's <c>UserTemplate</c>. Neither is an error, as before the template option.
    /// </summary>
    internal static string ResolveQuestion(LlmQueryOptions options,
        AiPromptTemplateSettings? template)
    {
        if (!string.IsNullOrEmpty(options.Question))
        {
            return options.Question;
        }

        if (!string.IsNullOrEmpty(template?.UserTemplate))
        {
            return template.UserTemplate;
        }

        throw new ArgumentException(template == null
                ? "Question is required. Set 'question' or 'promptTemplateName' on the node."
                : $"Question is required: AiPromptTemplate '{template.Name}' has no UserTemplate and the node sets no 'question'.",
            nameof(options.Question));
    }

    /// <summary>
    ///     The system prompt of this run before the no-tools correction (AB#8498): an inline
    ///     <c>systemPrompt</c> wins (even an empty one, which sends no system prompt, as before);
    ///     otherwise the template's <c>SystemPromptFragment</c> with its <c>FewShotExamples</c>;
    ///     otherwise <see cref="LlmQueryOptions.DefaultSystemPrompt" />. A node
    ///     without template and without <c>systemPrompt</c> therefore sends exactly the old default.
    /// </summary>
    internal static string ResolveSystemPrompt(LlmQueryOptions options,
        AiPromptTemplateSettings? template)
    {
        if (options.SystemPrompt != null)
        {
            return options.SystemPrompt;
        }

        if (template == null)
        {
            return options.DefaultSystemPrompt;
        }

        var systemPrompt = template.SystemPrompt ?? options.DefaultSystemPrompt;
        return string.IsNullOrEmpty(template.FewShotExamples)
            ? systemPrompt
            : $"{systemPrompt}\n\n<examples>\n{template.FewShotExamples}\n</examples>";
    }

    /// <summary>
    /// The system-prompt suffix appended when MCP is configured but no tools are available.
    /// Without it, a tools-promising pipeline prompt makes the model imitate tool calls as
    /// plain text and fabricate their results (AB#4562 follow-up; observed in the accounting
    /// chat: invented invoice data). Kept as a constant so tests and callers stay in sync.
    /// </summary>
    internal const string NoToolsSystemPromptSuffix =
        "\n\nIMPORTANT: No live data tools are available in this session (the tool service could not " +
        "be reached). Do NOT simulate, imitate or fabricate tool calls or tool results, and do NOT " +
        "invent any data. If answering requires live data, state clearly that live data access is " +
        "currently unavailable and answer only from the content provided in this conversation.";

    /// <summary>
    /// Returns the system prompt to send to the model. When MCP is configured for the node but
    /// zero tools were loaded (server unreachable, handshake rejected, name filter matched
    /// nothing), the <see cref="NoToolsSystemPromptSuffix" /> is appended so the model does not
    /// act on a prompt that promises tools it does not have. Returns the original reference
    /// unchanged in all other cases (callers detect modification via ReferenceEquals).
    /// </summary>
    internal static string BuildEffectiveSystemPrompt(string systemPrompt, bool mcpConfigured, int mcpToolCount)
    {
        if (!mcpConfigured || mcpToolCount > 0)
        {
            return systemPrompt;
        }

        return string.IsNullOrEmpty(systemPrompt)
            ? NoToolsSystemPromptSuffix.TrimStart()
            : systemPrompt + NoToolsSystemPromptSuffix;
    }

    /// <summary>
    /// Resolves the optional "main content" for the AI prompt from <paramref name="path" />.
    /// The node's base-class default <c>Path</c> is <c>"$"</c> (the whole root object); reading that
    /// as a string throws ("Cannot get the value of a token type 'StartObject' as a string"), which
    /// broke MCP-only pipelines that never set an explicit path. Rules:
    /// <list type="bullet">
    /// <item>empty path → no main content;</item>
    /// <item>path resolves to a string scalar → that string;</item>
    /// <item>explicit (non-<c>"$"</c>) path to any other concrete value → rendered as indented JSON;</item>
    /// <item>default <c>"$"</c> root object, or an absent path → no main content (rely on MCP tools).</item>
    /// </list>
    /// </summary>
    internal static string? ResolveMainContent(IDataContext dataContext, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var kind = dataContext.GetKind(path);
        if (kind == DataKind.String)
        {
            return dataContext.Get<string>(path);
        }

        if (kind != DataKind.Undefined && path != "$")
        {
            return JsonSerializer.Serialize(dataContext.Get<object?>(path), IndentedPromptOptions);
        }

        return null;
    }

    internal static string BuildUserPrompt(string question, string context, string responseFormat,
        string jsonExample, string? jsonSchema = null)
    {
        var (sourcePart, instructionPart) =
            BuildUserPromptParts(question, context, responseFormat, jsonExample, jsonSchema);
        return sourcePart + instructionPart;
    }

    /// <summary>
    ///     The user prompt in two parts (AB#8658): the source part (preamble and the first
    ///     <paramref name="cacheableContextLength" /> characters of the context — the main content) and the
    ///     instruction part (the rest of the context, the question and the answer format). Their
    ///     concatenation is exactly the prompt this node has always sent; the split lets <c>cacheSource</c>
    ///     put the cache breakpoint after the stable source. Without a length the whole context is the source.
    /// </summary>
    internal static (string SourcePart, string InstructionPart) BuildUserPromptParts(string question,
        string context, string responseFormat, string jsonExample, string? jsonSchema = null,
        int? cacheableContextLength = null)
    {
        var split = Math.Clamp(cacheableContextLength ?? context.Length, 0, context.Length);

        var sourceBuilder = new StringBuilder();
        sourceBuilder.AppendLine("Please analyze the following content and answer the question provided.");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("CONTENT:");
        sourceBuilder.AppendLine("---");
        sourceBuilder.Append(context, 0, split);

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(context[split..]);
        promptBuilder.AppendLine("---");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("QUESTION:");
        promptBuilder.AppendLine(question);
        promptBuilder.AppendLine();

        if (jsonSchema != null)
        {
            // AB#8498: the schema is the contract; the (invoice-shaped) default sample would only
            // contradict it.
            promptBuilder.AppendLine("Please provide your response as JSON that satisfies this JSON Schema:");
            promptBuilder.AppendLine(jsonSchema);
        }
        else if (responseFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
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

        return (sourceBuilder.ToString(), promptBuilder.ToString());
    }

    /// <summary>
    ///     The earlier turns of the conversation at <paramref name="historyPath" />: entries with a
    ///     <c>role</c> and a <c>content</c>. The content is a string or a content-parts array, whose text
    ///     parts are joined. An entry without a role or without text is left out; a role is passed on as
    ///     written. Not an array at the path: no history and no log line.
    /// </summary>
    internal static List<ChatMessage>? LoadHistory(string? historyPath, IDataContext dataContext,
        INodeContext nodeContext)
    {
        if (string.IsNullOrEmpty(historyPath) || dataContext.GetKind(historyPath) != DataKind.Array)
        {
            return null;
        }

        var result = new List<ChatMessage>();
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

            result.Add(new ChatMessage(new ChatRole(role), content));
        }

        nodeContext.Debug($"Loaded {result.Count} messages from conversation history");
        return result;
    }

    /// <summary>
    ///     Flattens a content-parts array (<c>[{ "type": "text", "text": "…" }, …]</c>) into one string:
    ///     the text parts, one per line. Parts of another type are left out.
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

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using SchemaDocument = NJsonSchema.JsonSchema;
using SchemaValidationError = NJsonSchema.Validation.ValidationError;
using ChildSchemaValidationError = NJsonSchema.Validation.ChildSchemaValidationError;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     Guaranteed JSON for <c>AnthropicAiQuery@1</c> with a <c>jsonSchema</c> (AB#8498). Ported from
///     the KnowledgeCapture prototype (<c>philipp/AB#4861_llmquery-guaranteed-json</c>,
///     <c>LlmJsonResponseProcessor</c> / <c>LlmJsonExtractor</c>, Philipp Schwaab) and extended by the
///     schema validation the prototype left to the provider.
/// </summary>
/// <remarks>
///     <para>
///         Escalation ladder, cheapest first: strict parse, then JSON extraction from prose, then
///         mechanical repair (naked quotes, trailing commas, unclosed strings and brackets), then the
///         repeated-key normalisation of AB#5348, then validation against the schema. Only when the
///         answer still does not parse or does not validate, a <b>bounded</b> repair call runs
///         (at most <see cref="MaxRepairAttemptsCap" />), which sends the broken answer, the errors
///         and the schema back — never the original prompt or context, so a repair costs about the
///         size of the answer, not of the input.
///     </para>
///     <para>
///         Exhausted repairs fail with <see cref="JsonSchemaViolationException" />, whose message names
///         the violated path(s). A schema-mode answer is never handed on as text: a downstream node
///         relies on the schema.
///     </para>
/// </remarks>
internal static class AiJsonSchemaResponseProcessor
{
    /// <summary>Hard cap of repair calls whatever the node configures (prototype contract).</summary>
    internal const int MaxRepairAttemptsCap = 2;

    /// <summary>At most this many violations are listed in errors and repair prompts.</summary>
    private const int MaxListedErrors = 10;

    internal const string RepairSystemPrompt =
        "You repair a JSON document. Return ONLY the corrected JSON document - no prose, no code " +
        "fences. It must be valid JSON and satisfy the given JSON Schema. Preserve the content; " +
        "change only what the listed errors require (syntax, missing or misnamed properties, wrong " +
        "types, values outside the allowed set).";

    // Keywords the structured-outputs endpoint rejects or ignores; they stay in the client-side
    // validation, which sees the schema exactly as the pipeline author wrote it.
    private static readonly string[] UnsupportedApiKeywords =
    [
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern",
        "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties"
    ];

    // Keywords whose value is a map from author-chosen names to sub-schemas.
    private static readonly HashSet<string> SchemaMapKeywords =
        ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"];

    /// <summary>
    ///     Parses the configured schema. Throws <see cref="ArgumentException" /> naming the problem when
    ///     the string is not a JSON Schema, so a misconfigured node fails before any API call.
    /// </summary>
    internal static SchemaDocument ParseSchema(string schemaJson)
    {
        try
        {
            using var _ = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"jsonSchema is not valid JSON: {ex.Message}", "jsonSchema", ex);
        }

        try
        {
            return SchemaDocument.FromJsonAsync(schemaJson).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"jsonSchema is not a usable JSON Schema: {ex.Message}", "jsonSchema",
                ex);
        }
    }

    /// <summary>
    ///     The copy of the schema sent to the API for constrained decoding
    ///     (<c>output_config.format.schema</c>): keywords the endpoint does not support are removed and
    ///     every object schema gets <c>additionalProperties: false</c>, which the endpoint requires.
    ///     The authored schema itself stays unchanged for the client-side validation.
    /// </summary>
    internal static JsonNode BuildApiSchema(string schemaJson)
    {
        var root = JsonNode.Parse(schemaJson)
                   ?? throw new ArgumentException("jsonSchema is empty", "jsonSchema");
        Normalise(root);
        return root;

        static void Normalise(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    {
                        foreach (var keyword in UnsupportedApiKeywords)
                        {
                            obj.Remove(keyword);
                        }

                        obj.Remove("$schema");
                        obj.Remove("$id");

                        var isObjectSchema = obj.ContainsKey("properties") ||
                                             (obj["type"] is JsonValue type &&
                                              type.TryGetValue<string>(out var typeName) && typeName == "object");
                        if (isObjectSchema)
                        {
                            obj["additionalProperties"] = false;
                        }

                        foreach (var property in obj.ToList())
                        {
                            if (SchemaMapKeywords.Contains(property.Key) && property.Value is JsonObject map)
                            {
                                // The keys of "properties"/"$defs" are names chosen by the author, not
                                // keywords: normalise each sub-schema, never the map itself.
                                foreach (var entry in map)
                                {
                                    Normalise(entry.Value);
                                }

                                continue;
                            }

                            Normalise(property.Value);
                        }

                        break;
                    }
                case JsonArray array:
                    {
                        foreach (var item in array)
                        {
                            Normalise(item);
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>
    ///     Turns the model answer into a schema-valid JSON value, with at most
    ///     <paramref name="maxRepairAttempts" /> (capped at <see cref="MaxRepairAttemptsCap" />) calls
    ///     of <paramref name="repairCall" />, which receives only the repair prompt built here.
    /// </summary>
    /// <returns>The validated value (a detached <see cref="JsonElement" /> or <see cref="JsonNode" />).</returns>
    /// <exception cref="JsonSchemaViolationException">The answer is still invalid after the repairs.</exception>
    internal static async Task<object> ProcessAsync(string aiResponse, string schemaJson, SchemaDocument schema,
        int maxRepairAttempts, Func<string, Task<string>> repairCall, INodeContext nodeContext)
    {
        var maxAttempts = Math.Clamp(maxRepairAttempts, 0, MaxRepairAttemptsCap);
        var current = aiResponse;

        for (var attempt = 0; ; attempt++)
        {
            var errors = Evaluate(current, schema, nodeContext, out var value);
            if (errors.Count == 0)
            {
                if (attempt > 0)
                {
                    nodeContext.Info($"JSON answer repaired and schema-valid after repair call {attempt}");
                }

                return value!;
            }

            if (attempt >= maxAttempts)
            {
                throw new JsonSchemaViolationException(errors, attempt);
            }

            nodeContext.Warning(
                $"AI answer is not schema-valid JSON ({errors[0]}); bounded repair call " +
                $"{attempt + 1}/{maxAttempts} (sending only the broken answer, the errors and the schema)");

            current = await repairCall(BuildRepairPrompt(current, errors, schemaJson));
        }
    }

    /// <summary>
    ///     The user message of a repair call: the errors, the schema and the broken answer — nothing
    ///     from the original request (cost contract of the prototype).
    /// </summary>
    internal static string BuildRepairPrompt(string brokenAnswer, IReadOnlyList<string> errors, string schemaJson)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The following JSON document is invalid.");
        builder.AppendLine();
        builder.AppendLine("ERRORS:");
        foreach (var error in errors)
        {
            builder.Append("- ").AppendLine(error);
        }

        builder.AppendLine();
        builder.AppendLine("JSON SCHEMA:");
        builder.AppendLine(schemaJson);
        builder.AppendLine();
        builder.AppendLine("DOCUMENT:");
        builder.AppendLine(brokenAnswer);
        return builder.ToString();
    }

    /// <summary>
    ///     Parses (with the deterministic tiers) and validates one answer. Returns the error list,
    ///     empty when the answer is valid; <paramref name="value" /> is then the value to write.
    /// </summary>
    internal static List<string> Evaluate(string answer, SchemaDocument schema, INodeContext nodeContext,
        out object? value)
    {
        value = null;
        if (!TryParse(answer, nodeContext, out var element, out var parseError))
        {
            return [$"$: not valid JSON ({parseError})"];
        }

        var sanitized = SanitizeDuplicateKeys(element, nodeContext);
        var serialized = sanitized is JsonElement e ? e.GetRawText() : ((JsonNode)sanitized).ToJsonString();

        var errors = Flatten(schema.Validate(serialized))
            .Take(MaxListedErrors)
            .ToList();
        if (errors.Count == 0)
        {
            value = sanitized;
        }

        return errors;
    }

    private static IEnumerable<string> Flatten(IEnumerable<SchemaValidationError> errors)
    {
        foreach (var error in errors)
        {
            if (error is ChildSchemaValidationError child && child.Errors.Count > 0)
            {
                // anyOf/oneOf: the composite error itself names the path; the branch details follow.
                yield return Describe(error);
                foreach (var nested in child.Errors.Values.SelectMany(Flatten))
                {
                    yield return nested;
                }

                continue;
            }

            yield return Describe(error);
        }

        static string Describe(SchemaValidationError error)
        {
            return $"{ToJsonPath(error.Path)}: {error.Kind}";
        }
    }

    /// <summary>
    ///     NJsonSchema reports paths as <c>#/items[0].name</c>; the pipeline speaks JSONPath
    ///     (<c>$.items[0].name</c>), so the error names the path the way a pipeline author reads it.
    /// </summary>
    internal static string ToJsonPath(string? schemaPath)
    {
        if (string.IsNullOrEmpty(schemaPath) || schemaPath == "#")
        {
            return "$";
        }

        var path = schemaPath.StartsWith("#/", StringComparison.Ordinal) ? schemaPath[2..] : schemaPath.TrimStart('#');
        return path.StartsWith('[') ? "$" + path : "$." + path;
    }

    private static object SanitizeDuplicateKeys(JsonElement element, INodeContext nodeContext)
    {
        if (!JsonDuplicateKeys.TryFindDuplicateKey(element, out var duplicateKey))
        {
            return element.Clone();
        }

        nodeContext.Warning(
            $"The AI response repeats the JSON key '{duplicateKey}' in one object. Keeping the last value " +
            "of every repeated key (AB#5348).");
        return JsonDuplicateKeys.RewriteLastValueWins(element) ?? (object)element.Clone();
    }

    /// <summary>Strict parse, then extraction from mixed prose, then mechanical repair.</summary>
    internal static bool TryParse(string text, INodeContext nodeContext, out JsonElement element,
        out string? error)
    {
        if (TryParseStrict(text, out element, out error))
        {
            return true;
        }

        var extracted = LlmJsonExtractor.ExtractJsonFromText(text);
        var candidate = extracted ?? text;
        if (extracted != null && TryParseStrict(extracted, out element, out _))
        {
            nodeContext.Debug("Extracted JSON from a mixed prose/JSON answer");
            error = null;
            return true;
        }

        var repaired = LlmJsonExtractor.RepairMechanically(candidate);
        if (!string.Equals(repaired, candidate, StringComparison.Ordinal) &&
            TryParseStrict(repaired, out element, out _))
        {
            nodeContext.Info("JSON answer repaired deterministically (no repair call)");
            error = null;
            return true;
        }

        element = default;
        return false;
    }

    private static bool TryParseStrict(string text, out JsonElement element, out string? error)
    {
        try
        {
            // JsonDocument keeps duplicate keys visible for SanitizeDuplicateKeys (AB#5348).
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            element = default;
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
///     A <c>jsonSchema</c> answer of <c>AnthropicAiQuery@1</c> that is still invalid after the bounded
///     repair calls. The message names the violated path(s) in JSONPath notation.
/// </summary>
internal sealed class JsonSchemaViolationException(IReadOnlyList<string> errors, int repairCalls)
    : InvalidOperationException(
        $"The AI answer does not satisfy the configured jsonSchema after {repairCalls} repair call(s). " +
        $"Violations: {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

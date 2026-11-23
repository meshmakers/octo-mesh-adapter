using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The sealed provider endpoint of an AiConfiguration (<c>providerEndpoint</c>, a secret): the endpoint and
///     the key that goes with it, set together so a key is never sent to a URL it was not configured for. The
///     value is the JSON text <c>{"baseUrl":"…","apiKey":"…"}</c>; <c>apiKey</c> absent or empty is a keyless
///     endpoint (no key header is sent).
/// </summary>
/// <param name="BaseUrl">The endpoint, checked by the rule of <see cref="LlmBaseUrl" />.</param>
/// <param name="ApiKey">The key for that endpoint; <c>null</c> for a keyless endpoint.</param>
internal sealed record LlmProviderEndpoint(Uri BaseUrl, string? ApiKey)
{
    /// <summary>The key of the sealed endpoint in the AiConfiguration payload.</summary>
    internal const string Property = "providerEndpoint";

    /// <summary>
    ///     The sealed endpoint of the AiConfiguration, or <c>null</c> when none is set (absent, JSON null or empty
    ///     text). The whole value and the key are registered as secrets of the execution before anything else is
    ///     logged. A value that is set but not valid fails with a message that names only the position or the
    ///     rule, never a part of the value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is set and not valid; nothing has been sent.</exception>
    internal static LlmProviderEndpoint? Resolve(string? configurationName, IMeshEtlContext etlContext,
        INodeContext nodeContext)
    {
        var node = LlmQueryEngine.ReadAiConfigurationProperty(configurationName, Property, etlContext, nodeContext);
        if (node == null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid(configurationName, nodeContext, "it is not a JSON text");
        }

        var raw = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // The value holds the key: masked before any parse error, log line or request.
        nodeContext.RegisterSecret(raw);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw Invalid(configurationName, nodeContext,
                $"it is not valid JSON (line {ex.LineNumber}, byte {ex.BytePositionInLine})");
        }

        using (document)
        {
            return Read(document.RootElement, configurationName, nodeContext);
        }
    }

    private static LlmProviderEndpoint Read(JsonElement root, string? configurationName, INodeContext nodeContext)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(configurationName, nodeContext, "it is not a JSON object");
        }

        string? baseUrlText = null;
        string? apiKey = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (!seen.Add(member.Name))
            {
                throw Invalid(configurationName, nodeContext, "a member is given twice");
            }

            switch (member.Name)
            {
                case "baseUrl" when member.Value.ValueKind == JsonValueKind.String:
                    baseUrlText = member.Value.GetString();
                    break;
                case "apiKey" when member.Value.ValueKind == JsonValueKind.String:
                    apiKey = member.Value.GetString();
                    break;
                case "baseUrl":
                case "apiKey":
                    throw Invalid(configurationName, nodeContext, $"'{member.Name}' is not a JSON text");
                default:
                    throw Invalid(configurationName, nodeContext,
                        "it has a member other than 'baseUrl' and 'apiKey'");
            }
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            nodeContext.RegisterSecret(apiKey);
        }

        Uri? baseUrl;
        try
        {
            baseUrl = LlmBaseUrl.Parse(baseUrlText);
        }
        catch (ArgumentException ex)
        {
            throw Invalid(configurationName, nodeContext, ex.Message);
        }

        if (baseUrl == null)
        {
            throw Invalid(configurationName, nodeContext, "'baseUrl' is missing or empty");
        }

        return new LlmProviderEndpoint(baseUrl, string.IsNullOrEmpty(apiKey) ? null : apiKey);
    }

    private static InvalidOperationException Invalid(string? configurationName, INodeContext nodeContext,
        string reason)
    {
        return new InvalidOperationException(
            $"[{nodeContext.NodePath}]: The providerEndpoint of AiConfiguration '{configurationName}' is not valid: " +
            $"{reason}. Expected the JSON text {{\"baseUrl\":\"https://…\",\"apiKey\":\"…\"}}. No request was sent.");
    }
}

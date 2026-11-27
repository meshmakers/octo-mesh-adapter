using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     How a chat client reaches the AI service's central-key LLM gateway (AB#8691): the gateway's base URL and an
///     optional caller reference for the usage record. The credential is the pipeline service account's token, passed
///     to the client in place of an API key and sent as a bearer token; the central key itself never leaves the
///     gateway.
/// </summary>
/// <param name="BaseUrl">Base URL of the AI service (<c>Adapter:LlmGatewayUrl</c>).</param>
/// <param name="CallerRef">
///     Attribution sent as <see cref="CallerRefHeader" />; <c>null</c> sends none. Only values the gateway accepts
///     (<see cref="IsValidCallerRef" />) are set.
/// </param>
internal sealed record LlmGatewayAccess(Uri BaseUrl, string? CallerRef)
{
    /// <summary>Header of the caller reference; attribution only, never authorization.</summary>
    internal const string CallerRefHeader = "X-Octo-Caller-Ref";

    /// <summary>Gateway code: the call does not fit into the tenant's remaining monthly budget.</summary>
    internal const string SpendCapReachedCode = "AiSpendCapReached";

    /// <summary>Gateway code: cap, ledger or token count could not be read; the gateway failed closed.</summary>
    internal const string MeterUnavailableCode = "AiMeterUnavailable";

    private static readonly Regex CallerRefPattern = new("^[A-Za-z0-9._:/-]{1,128}$", RegexOptions.CultureInvariant);

    /// <summary>Whether the gateway accepts <paramref name="value" /> as caller reference.</summary>
    internal static bool IsValidCallerRef(string? value) => value != null && CallerRefPattern.IsMatch(value);

    /// <summary>
    ///     Reads a gateway refusal: an error body with an OctoMesh <c>code</c> (<c>{"type":"error","error":{…},
    ///     "code":…,"details":{…}}</c>). A provider error the gateway passed through carries no <c>code</c> and is
    ///     left to the client's provider error handling.
    /// </summary>
    /// <param name="statusCode">HTTP status of the gateway's answer.</param>
    /// <param name="responseBody">Body of the answer.</param>
    /// <param name="refusal">The refusal with the node's message; set when the method returns true.</param>
    internal static bool TryReadRefusal(HttpStatusCode statusCode, string? responseBody,
        out LlmGatewayRefusedException refusal)
    {
        refusal = null!;
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(codeElement.GetString()))
            {
                return false;
            }

            var code = codeElement.GetString()!;
            root.TryGetProperty("details", out var details);
            var gatewayMessage = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                                 error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            refusal = new LlmGatewayRefusedException(code, (int)statusCode,
                BuildMessage(code, (int)statusCode, details, gatewayMessage));
            return true;
        }
    }

    private static string BuildMessage(string code, int status, JsonElement details, string? gatewayMessage)
    {
        switch (code)
        {
            case SpendCapReachedCode:
            {
                var cap = ReadDecimal(details, "capEur");
                var spent = ReadDecimal(details, "spentEur");
                var resetsAt = ReadResetDate(details);
                return string.Create(CultureInfo.InvariantCulture,
                    $"Monthly LLM cap of EUR {Format(cap)} reached for this tenant (spent EUR {Format(spent)}); " +
                    $"resets on {resetsAt} UTC. Ask a Meshmakers platform admin to raise it. ({SpendCapReachedCode})");
            }
            case MeterUnavailableCode:
                return "LLM spend on the central key cannot be metered right now, so the AI service refused the call " +
                       $"before any provider request (fail closed). Retry later. ({MeterUnavailableCode})";
            default:
                return $"The AI service's LLM gateway refused the call (HTTP {status}, {code})" +
                       (string.IsNullOrWhiteSpace(gatewayMessage) ? "." : $": {gatewayMessage}");
        }
    }

    private static decimal? ReadDecimal(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string Format(decimal? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "?";

    private static string ReadResetDate(JsonElement details) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty("resetsAt", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var resetsAt)
            ? resetsAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "?";
}

/// <summary>
///     The central-key LLM gateway refused a call with an OctoMesh error code (AB#8691), e.g.
///     <see cref="LlmGatewayAccess.SpendCapReachedCode" />. Fails the node like any provider error; nothing is
///     retried.
/// </summary>
internal sealed class LlmGatewayRefusedException(string code, int statusCode, string message)
    : HttpRequestException(message)
{
    /// <summary>The gateway's OctoMesh error code.</summary>
    public string Code { get; } = code;

    /// <summary>The HTTP status of the refusal.</summary>
    public int GatewayStatusCode { get; } = statusCode;
}

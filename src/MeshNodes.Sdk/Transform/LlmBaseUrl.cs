namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
///     The rule for the <c>baseUrl</c> of the sealed <c>providerEndpoint</c> of the AiConfiguration <c>LlmQuery@1</c> uses. The provider receives the API key, so the URL
///     must be one the key may travel to: absolute, https (plain http only on a loopback address, for a
///     local gateway during development), and without user info, query or fragment, which would either
///     carry a second credential or be cut off when the endpoint path is appended.
/// </summary>
/// <remarks>
///     The rule of the https check is lifted from the LlmQuery prototype by Philipp Schwaab
///     (<c>LlmClientFactory.RequireHttpsForKeyedEndpoint</c>). The node checks the value before its first
///     request, so a wrong URL fails the node and no request is sent.
/// </remarks>
public static class LlmBaseUrl
{
    /// <summary>
    ///     The base URL as the node uses it, or <c>null</c> when none is set (null, empty or white space).
    /// </summary>
    /// <exception cref="ArgumentException">The value is set and breaks the rule.</exception>
    public static Uri? Parse(string? value)
    {
        if (!TryParse(value, out var uri, out var rule))
        {
            throw Invalid(value!, rule!);
        }

        return uri;
    }

    /// <summary>
    ///     The rule without the value: <c>true</c> with the URL (or <c>null</c> when none is set), or <c>false</c>
    ///     with the rule the value breaks. The rule text never contains a part of the value, so a caller whose value
    ///     may be a secret (a key pasted into the wrong field) can report it.
    /// </summary>
    public static bool TryParse(string? value, out Uri? uri, out string? rule)
    {
        uri = null;
        rule = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Checked on the text as well: on Unix a path such as "/v1" parses as an absolute file URI.
        if (!value.Contains("://", StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            rule = "it is not an absolute URL";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            rule = "the API key would leave the adapter without TLS. Use https, or http only on a loopback address";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            rule = "it carries user info";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            rule = "it carries a query or a fragment";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static ArgumentException Invalid(string value, string reason, string paramName = "value")
    {
        // The value is named because it is configuration, not a secret; user info, a query or a fragment
        // may hold a credential, so a value with one of them is not repeated.
        var shown = value.IndexOfAny(['@', '?', '#']) >= 0 ? "(not shown: it may hold a credential)" : $"'{value}'";
        return new ArgumentException($"baseUrl {shown} is not allowed: {reason}.", paramName);
    }
}

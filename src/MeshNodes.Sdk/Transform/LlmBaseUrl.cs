namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
///     The rule for the <c>baseUrl</c> of the AiConfiguration <c>LlmQuery@1</c> uses. The provider receives the API key, so the URL
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Checked on the text as well: on Unix a path such as "/v1" parses as an absolute file URI.
        if (!value.Contains("://", StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw Invalid(value, "it is not an absolute URL");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw Invalid(value,
                "the API key would leave the adapter without TLS. Use https, or http only on a loopback address");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid(value, "it carries user info");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            throw Invalid(value, "it carries a query or a fragment");
        }

        return uri;
    }

    private static ArgumentException Invalid(string value, string reason)
    {
        // The value is named because it is configuration, not a secret; user info, a query or a fragment
        // may hold a credential, so a value with one of them is not repeated.
        var shown = value.IndexOfAny(['@', '?', '#']) >= 0 ? "(not shown: it may hold a credential)" : $"'{value}'";
        return new ArgumentException($"baseUrl {shown} is not allowed: {reason}.", "baseUrl");
    }
}

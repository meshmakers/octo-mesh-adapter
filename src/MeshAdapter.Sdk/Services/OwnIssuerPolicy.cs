using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     The adapter's own identity service — the only issuer a service-account token may come from (AB#9776,
///     decision "own issuer only", 2026-11-24).
/// </summary>
/// <remarks>
///     <para>
///         <b>Endpoint.</b> Every service-account token request goes to <see cref="Endpoint" />:
///         <c>Adapter:IssuerUri</c> (the identity service the adapter authenticates itself against, AB#5072), else
///         <c>Adapter:AuthorityUrl</c> (the authority incoming bearers are validated against). A configured
///         <c>IssuerUri</c> on a <c>ServiceAccountConfiguration</c> never becomes the address of a request — it is
///         only checked to be one of the own names — so neither the service account's secret nor the adapter's own
///         client credentials can be sent anywhere else, whatever the tenant wrote into the entity.
///     </para>
///     <para>
///         <b>Names.</b> The own identity service is known under every value the adapter already trusts for its own
///         bearers: <c>Adapter:IssuerUri</c>, <c>Adapter:AuthorityUrl</c> and <c>Adapter:AdditionalValidIssuers</c>
///         (split-horizon, AB#4922/AB#5232 — e.g. the controller resolves <c>{{service.authority}}</c> to the public
///         URL while the adapter reaches the service under an internal one). An empty value and the unresolved
///         <c>{{service.authority}}</c> token mean "the own installation" (AB#5115).
///     </para>
///     <para>
///         <b>Comparison.</b> Both sides are normalized: absolute <c>http</c>/<c>https</c> URL without user info,
///         query or fragment; scheme and host compared without case, the default port made explicit, trailing
///         slashes of the path dropped, the path itself compared ordinally. Anything that does not normalize is
///         not the own issuer — the check fails closed.
///     </para>
/// </remarks>
internal sealed class OwnIssuerPolicy
{
    /// <summary>
    ///     Deploy-time template token an older communication controller projects UNRESOLVED into a
    ///     configuration's <c>IssuerUri</c> — the same statement as an empty value (AB#5115). Recognised in
    ///     the spellings the controller recognises (<see cref="UnresolvedAuthorityTemplatePattern" />).
    /// </summary>
    internal const string UnresolvedAuthorityTemplateToken = "{{service.authority}}";

    /// <summary>
    ///     The template token with any case and blanks around and inside the braces — byte-identical to
    ///     <c>PipelineServiceAccountProvisioningService.IssuerUriTokenPattern</c> in the communication controller,
    ///     so both sides agree on what means "the own installation".
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex UnresolvedAuthorityTemplatePattern = new(
        @"^\s*\{\{\s*service\.authority\s*\}\}\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly HashSet<string> _ownNames;

    internal OwnIssuerPolicy(AdapterOptions adapterOptions, MeshAdapterConfiguration meshAdapterConfiguration)
    {
        Endpoint = !string.IsNullOrWhiteSpace(adapterOptions.IssuerUri)
            ? adapterOptions.IssuerUri.Trim()
            : !string.IsNullOrWhiteSpace(meshAdapterConfiguration.AuthorityUrl)
                ? meshAdapterConfiguration.AuthorityUrl.Trim()
                : null;

        _ownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { adapterOptions.IssuerUri, meshAdapterConfiguration.AuthorityUrl }
                     .Concat(adapterOptions.AdditionalValidIssuers ?? [])
                     .Concat(meshAdapterConfiguration.AdditionalValidIssuers ?? []))
        {
            if (TryNormalize(name, out var normalized))
            {
                _ownNames.Add(normalized);
            }
        }
    }

    /// <summary>
    ///     The address every service-account token request goes to, or <c>null</c> when neither
    ///     <c>Adapter:IssuerUri</c> nor <c>Adapter:AuthorityUrl</c> is configured.
    /// </summary>
    internal string? Endpoint { get; }

    /// <summary>
    ///     Whether <paramref name="configuredIssuerUri" /> of a <c>ServiceAccountConfiguration</c> names the own
    ///     installation: empty or whitespace, the unresolved <c>{{service.authority}}</c> token, or one of the own
    ///     names after normalization.
    /// </summary>
    internal bool IsOwnConfiguredIssuer(string? configuredIssuerUri)
    {
        if (string.IsNullOrWhiteSpace(configuredIssuerUri))
        {
            return true;
        }

        if (UnresolvedAuthorityTemplatePattern.IsMatch(configuredIssuerUri))
        {
            return true;
        }

        return IsOwnIssuer(configuredIssuerUri);
    }

    /// <summary>
    ///     Whether <paramref name="issuer" /> — a configured URL or the <c>iss</c> claim of a token — is one of the
    ///     own names. Empty, the template token and anything that does not normalize are not.
    /// </summary>
    internal bool IsOwnIssuer(string? issuer)
    {
        return TryNormalize(issuer, out var normalized) && _ownNames.Contains(normalized);
    }

    /// <summary>
    ///     Normalizes an issuer URL for comparison, or returns <c>false</c> when it is not an absolute
    ///     <c>http</c>/<c>https</c> URL without user info, query and fragment.
    /// </summary>
    internal static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.IdnHost))
        {
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        return true;
    }
}

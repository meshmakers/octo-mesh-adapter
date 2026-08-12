using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

/// <summary>
/// Builds the RFC 6750 §3 challenge a denied route answers with.
///
/// The reason is written twice on purpose: <c>error_description</c> carries prose for whoever
/// reads a log, and <c>error_code</c> carries a value this adapter owns. The framework's own
/// wording is deliberately not the machine-readable half - it is English prose that a
/// Microsoft.IdentityModel upgrade may reword, and a client keyed on it would break silently
/// on a dependency bump nobody connected to authentication.
/// </summary>
internal static class BearerChallenge
{
    /// <summary>
    /// The one denial a client can act on: a new access token has a later expiry, so refreshing
    /// and retrying can succeed. Every other code below describes a condition the caller's next
    /// token shares, which is what stops a client from spending a grant per user action on a
    /// server-side misconfiguration.
    /// </summary>
    internal const string TokenExpired = "token_expired";

    internal const string TenantMismatch = "tenant_mismatch";
    internal const string RoleMissing = "role_missing";

    /// <summary>
    /// Sent when the request carried no credentials at all. RFC 6750 §3.1 asks for a bare
    /// challenge here rather than an error code - there is no token to describe.
    /// </summary>
    internal const string NoCredentials = "Bearer";

    /// <summary>
    /// Maps a token validation failure onto a stable code. A null failure means the handler
    /// reported no result rather than a rejection, which is the no-credentials case.
    /// </summary>
    internal static string? CodeFor(Exception? failure) => failure switch
    {
        null => null,
        SecurityTokenExpiredException => TokenExpired,
        SecurityTokenInvalidIssuerException => "issuer_invalid",
        SecurityTokenInvalidAudienceException => "audience_invalid",
        SecurityTokenSignatureKeyNotFoundException => "signature_key_not_found",
        SecurityTokenInvalidSignatureException => "signature_invalid",
        SecurityTokenNotYetValidException => "token_not_yet_valid",
        _ => "token_invalid"
    };

    /// <summary>Challenge for a 401: the token is missing, malformed or rejected.</summary>
    internal static string ForInvalidToken(Exception? failure)
    {
        var code = CodeFor(failure);
        if (code == null)
        {
            return NoCredentials;
        }

        return $"Bearer error=\"invalid_token\", error_description=\"{DescriptionFor(code)}\", " +
               $"error_code=\"{code}\"";
    }

    /// <summary>
    /// Challenge for a 403: the token is valid, the caller is not entitled. Required roles are
    /// named so the caller need not guess which of the two 403 causes it hit.
    /// </summary>
    internal static string ForInsufficientScope(string code, IReadOnlyCollection<string> requiredRoles)
    {
        var challenge = $"Bearer error=\"insufficient_scope\", error_description=\"{DescriptionFor(code)}\", " +
                        $"error_code=\"{code}\"";

        // A blank entry cannot grant access and is skipped by the gate, so naming it here would
        // send the operator looking for a role that does not exist.
        var roles = requiredRoles.Where(role => !string.IsNullOrWhiteSpace(role)).ToArray();
        return roles.Length > 0
            ? $"{challenge}, required_roles=\"{string.Join(' ', roles)}\""
            : challenge;
    }

    private static string DescriptionFor(string code) => code switch
    {
        TokenExpired => "The access token has expired",
        "issuer_invalid" => "The access token was issued by another authority",
        "audience_invalid" => "The access token was issued for another audience",
        "signature_key_not_found" => "The access token was signed with an unknown key",
        "signature_invalid" => "The access token signature is invalid",
        "token_not_yet_valid" => "The access token is not valid yet",
        TenantMismatch => "The access token does not serve this tenant",
        RoleMissing => "The caller holds none of the required roles",
        _ => "The access token is not valid"
    };
}

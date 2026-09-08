using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Resolves a recipient user's <b>subjectId</b> against the verified-identifier directory
/// (<c>System.Identity/VerifiedExternalIdentifier</c>, AB#5122) and writes the user's verified
/// channel identifiers plus the AB#5149 <c>preferredChannel</c> — and the deterministically
/// resolved <c>effectiveChannel</c> — to <see cref="TargetPathNodeConfiguration.TargetPath" />
/// (AB#5152).
/// <para>
/// This is the reverse direction of the AB#5136 caller-binding lookups: those resolve a channel
/// identifier (phone number / EntraID oid / e-mail) to a user; this node resolves a user to the
/// identifiers a system-initiated message may be delivered on. The fallback chain is fixed and
/// never guesses: the user's BINDING-SPECIFIC preference (<c>PreferredChannelBindingId</c>,
/// System.Identity 2.18.0 — the rtId of the chosen <c>VerifiedExternalIdentifier</c>) wins when
/// the referenced binding is still valid; its kind yields the channel (PhoneNumber → "SIGNAL",
/// EntraIdObjectId → "TEAMS") and its identifier value is the EXACT delivery target. A dangling or
/// expired reference — and every user without a preference — resolves to <c>"EMAIL"</c>, which
/// every user has. OTP/security messages stay channel-forced and never go through this node.
/// </para>
/// Output object at <c>TargetPath</c>:
/// <code>
/// {
///   "found": bool,                       // the subjectId resolved to a user
///   "subjectId": "...",
///   "email": "...",                      // the user's directory e-mail (absent when unset)
///   "name": "...",                       // the user's user name (absent when unset)
///   "preferredChannel": "SIGNAL",        // channel derived from the preferred binding's kind
///                                        // (legacy strings verbatim); ABSENT when none
///   "effectiveChannel": "SIGNAL",        // "SIGNAL" | "TEAMS" | "EMAIL" — fallback chain applied
///   "channelAddress": "+43660...",       // the selected identifier value; ABSENT for EMAIL
///   "identifiers": [                     // all valid bindings, for diagnostics / future branches
///     { "kind": "PhoneNumber", "value": "+43660..." }
///   ]
/// }
/// </code>
/// <remarks>
/// TRANSITIONAL (pre-System.Identity-2.18.0 rollout skew): a user carrying only the legacy
/// kind-level <c>PreferredChannel</c> string ("TEAMS" | "SIGNAL") and no binding id gets the old
/// kind-level resolution; multiple valid bindings of the preferred kind (a user may enroll
/// several phone numbers) are then disambiguated DETERMINISTICALLY — the most recently verified
/// binding wins (<c>LastVerifiedAt</c>, falling back to <c>EnrolledAt</c>; ties broken by the
/// lowest binding rtId) and a warning names the chosen identifier and the number of alternatives.
/// The choice never varies between runs for unchanged data.
/// </remarks>
/// </summary>
[NodeName("ResolveNotificationChannel", 1)]
public record ResolveNotificationChannelNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Initializes the node with the default sink <c>$.recipient</c>.</summary>
    public ResolveNotificationChannelNodeConfiguration()
    {
        TargetPath = "$.recipient";
    }

    /// <summary>
    /// Literal subjectId (runtime id of the <c>System.Identity/User</c>, the same value the
    /// identity service issues as the token <c>sub</c>) of the recipient to resolve.
    /// </summary>
    [PropertyGroup("General", 0)]
    public string? SubjectId { get; set; }

    /// <summary>
    /// JSONPath to resolve the recipient's subjectId from the data context
    /// (e.g. <c>$.body.toSubjectIds[0]</c>). Takes precedence over <see cref="SubjectId" />
    /// when it resolves to a non-empty value.
    /// </summary>
    [PropertyGroup("General", 1, "jsonpath")]
    public string? SubjectIdPath { get; set; }
}

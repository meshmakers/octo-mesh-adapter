namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Resolves a user's subjectId (the runtime id of a <c>System.Identity/User</c> — the same
///     value the identity service issues as the token <c>sub</c>) to the user's verified channel
///     identifiers and outbound channel preference (AB#5152). This is the REVERSE direction of the
///     AB#5136 caller-binding lookups (<see cref="IPhoneUserLookup" /> /
///     <see cref="IEntraIdUserLookup" /> / <see cref="IEmailUserLookup" />), reading the same
///     AB#5122 verified-identifier directory: identifier → user there, user → identifiers here.
///     Narrow data-access seam so the ResolveNotificationChannel@1 node's routing logic can be
///     unit-tested without a live tenant repository.
/// </summary>
internal interface ISubjectChannelLookup
{
    /// <summary>
    ///     Finds the user behind <paramref name="subjectId" /> in <paramref name="tenantId" /> and
    ///     returns their profile fields plus every VALID (non-expired) verified identifier binding.
    ///     Returns <c>null</c> when the tenant has no repository / no System.Identity model, the
    ///     subjectId is no runtime id, or no such user exists.
    /// </summary>
    Task<SubjectChannelRecord?> FindBySubjectIdAsync(string tenantId, string subjectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The channel-resolution view of one user (AB#5152): profile fields for addressing plus the
///     verified identifier bindings a system-initiated message may be delivered on. Only valid
///     (non-expired) bindings are surfaced — an expired binding must never carry a delivery, so the
///     "preference respected only with a valid binding" rule is enforced structurally.
/// </summary>
/// <param name="SubjectId">The user's runtime id (echoed back).</param>
/// <param name="Email">The user's e-mail attribute, when set — the universal fallback address.</param>
/// <param name="Name">The user's user name, when set.</param>
/// <param name="PreferredChannelBindingId">
///     The BINDING-SPECIFIC preference (AB#5149/AB#5152, System.Identity 2.18.0): the rtId of the
///     <c>VerifiedExternalIdentifier</c> the user chose, verbatim and unresolved — the consumer
///     matches it against <paramref name="Identifiers" />, so a dangling or expired reference is
///     visible as "not among the valid bindings" and falls back deterministically.
/// </param>
/// <param name="LegacyPreferredChannel">
///     TRANSITIONAL (pre-2.18.0 rollout skew): the legacy kind-level preference string
///     ("TEAMS" | "SIGNAL") verbatim, or null. Only consulted when no binding-specific reference
///     is set.
/// </param>
/// <param name="Identifiers">All valid verified identifier bindings of the user.</param>
internal sealed record SubjectChannelRecord(
    string SubjectId,
    string? Email,
    string? Name,
    string? PreferredChannelBindingId,
    string? LegacyPreferredChannel,
    IReadOnlyList<SubjectChannelIdentifier> Identifiers);

/// <summary>
///     One valid verified identifier binding of a user (AB#5152). <see cref="BindingRtId" /> is
///     what the binding-specific preference (<c>PreferredChannelBindingId</c>, System.Identity
///     2.18.0) is matched against. The recency metadata serves the TRANSITIONAL legacy kind-level
///     path only: a user may hold SEVERAL bindings of one kind (e.g. two enrolled phone numbers),
///     and a kind-level preference is then disambiguated deterministically — most recently
///     verified wins (<see cref="LastVerifiedAt" /> falling back to <see cref="EnrolledAt" />,
///     ties broken by the lowest <see cref="BindingRtId" />), never varying between runs for
///     unchanged data.
/// </summary>
/// <param name="Kind">The identifier kind (phone number / e-mail / EntraID oid / cert).</param>
/// <param name="Value">The identifier value (e.g. the phone number).</param>
/// <param name="BindingRtId">The binding entity's rtId (deterministic tie-break key).</param>
/// <param name="EnrolledAt">When the binding was enrolled, when stamped.</param>
/// <param name="LastVerifiedAt">When the binding was last re-verified, when stamped.</param>
internal sealed record SubjectChannelIdentifier(
    ChannelIdentifierKind Kind,
    string Value,
    string BindingRtId,
    DateTime? EnrolledAt = null,
    DateTime? LastVerifiedAt = null);

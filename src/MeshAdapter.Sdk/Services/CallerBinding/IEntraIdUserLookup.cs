using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Resolves an EntraID object id (<c>oid</c>) to the OctoMesh user it is enrolled for, reading
///     the AB#5122 verified-identifier directory (<c>System.Identity/VerifiedExternalIdentifier</c>)
///     that the EntraID identity provider auto-provisions on login (AB#5124). This is the narrow
///     data-access seam behind <see cref="EntraIdVerifiedCallerDirectory" />: it exists so the
///     directory's resolution and trust logic can be unit-tested without a live tenant repository.
/// </summary>
/// <remarks>
///     The lookup deliberately returns nothing channel- or trust-derived beyond the STORED enrollment
///     trust dimension of the binding. The per-message trust is the caller-binding's concern and is
///     combined by <see cref="EntraIdVerifiedCallerDirectory" /> as <c>min(enrollment, message)</c> —
///     the same effective-trust rule AB#5122's own resolver applies.
/// </remarks>
internal interface IEntraIdUserLookup
{
    /// <summary>
    ///     Finds the user the EntraID <paramref name="objectId" /> resolves to in
    ///     <paramref name="tenantId" />, or <c>null</c> when the tenant has no verified-identifier
    ///     directory, no binding exists for the oid, or the binding is dangling (its user was removed).
    /// </summary>
    Task<EntraIdCallerRecord?> FindByObjectIdAsync(string tenantId, string objectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The bound user behind an EntraID object id plus the STORED enrollment-trust dimension of its
///     verified-identifier binding (AB#5124). Mapped to a <see cref="VerifiedPrincipal" /> by
///     <see cref="EntraIdVerifiedCallerDirectory" />, whose <see cref="SubjectId" /> is the user's
///     runtime id — the same value the identity service issues as the token <c>sub</c> — so an
///     owner-scoped write (<c>RtCreatedBy</c>, AB#4978) stamps the human, exactly as the HTTP bearer
///     path does.
/// </summary>
/// <param name="SubjectId">The user's runtime id, used as the caller subject.</param>
/// <param name="HomeTenantId">The tenant the user lives in (the directory's tenant).</param>
/// <param name="Email">The user's e-mail, when set.</param>
/// <param name="Name">The user's display name (user name), when set.</param>
/// <param name="Roles">The user's assigned role names.</param>
/// <param name="EnrollmentTrust">The stored enrollment trust of the binding (IdP-provisioned = Strong).</param>
internal sealed record EntraIdCallerRecord(
    string SubjectId,
    string? HomeTenantId,
    string? Email,
    string? Name,
    IReadOnlyList<string> Roles,
    CallerTrustLevel EnrollmentTrust);

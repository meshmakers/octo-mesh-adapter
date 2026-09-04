using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Resolves a phone number to the OctoMesh user it is enrolled for, reading the AB#5122
///     verified-identifier directory (<c>System.Identity/VerifiedExternalIdentifier</c>) that the
///     self-service phone enrollment (AB#5123) writes on a proven OTP. This is the narrow data-access
///     seam behind <see cref="PhoneVerifiedCallerDirectory" /> — the phone counterpart of
///     <see cref="IEntraIdUserLookup" /> (AB#5124) — so the directory's resolution and trust logic can
///     be unit-tested without a live tenant repository.
/// </summary>
/// <remarks>
///     Like the EntraID lookup it returns only the STORED enrollment-trust dimension of the binding;
///     the per-message trust (a Signal-verified sender) is the caller-binding's concern and is
///     combined by <see cref="PhoneVerifiedCallerDirectory" /> as <c>min(enrollment, message)</c>.
/// </remarks>
internal interface IPhoneUserLookup
{
    /// <summary>
    ///     Finds the user the normalized <paramref name="phoneNumber" /> resolves to in
    ///     <paramref name="tenantId" />, or <c>null</c> when the tenant has no verified-identifier
    ///     directory, no binding exists for the number, or the binding is dangling (user removed).
    /// </summary>
    Task<PhoneCallerRecord?> FindByPhoneNumberAsync(string tenantId, string phoneNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The bound user behind a phone number plus the STORED enrollment-trust dimension of its
///     verified-identifier binding (AB#5123). Mapped to a <see cref="VerifiedPrincipal" /> by
///     <see cref="PhoneVerifiedCallerDirectory" />, whose <see cref="SubjectId" /> is the user's
///     runtime id — the same value the identity service issues as the token <c>sub</c> — so an
///     owner-scoped write (<c>RtCreatedBy</c>, AB#4978) stamps the human, exactly as the HTTP bearer
///     path does.
/// </summary>
internal sealed record PhoneCallerRecord(
    string SubjectId,
    string? HomeTenantId,
    string? Email,
    string? Name,
    IReadOnlyList<string> Roles,
    CallerTrustLevel EnrollmentTrust);

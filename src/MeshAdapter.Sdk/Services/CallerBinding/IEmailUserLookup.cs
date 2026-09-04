using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Resolves an e-mail address to the OctoMesh user it is enrolled for, reading the AB#5122
///     verified-identifier directory (<c>System.Identity/VerifiedExternalIdentifier</c>) that the
///     admin-configured whitelist (AB#5125, <c>Source = Admin</c>, <c>EnrollmentTrust = Strong</c>) —
///     or, later, a self-service e-mail OTP enrollment — writes. This is the narrow data-access seam
///     behind <see cref="EmailVerifiedCallerDirectory" /> — the e-mail counterpart of
///     <see cref="IPhoneUserLookup" /> (AB#5123) — so the directory's resolution and trust logic can
///     be unit-tested without a live tenant repository.
/// </summary>
/// <remarks>
///     Like the phone lookup it returns only the STORED enrollment-trust dimension of the binding;
///     the per-message trust (a DKIM/DMARC-authenticated inbound mail) is the caller-binding's
///     concern and is combined by <see cref="EmailVerifiedCallerDirectory" /> as
///     <c>min(enrollment, message)</c>. The e-mail address is matched case-insensitively — the lookup
///     lower-cases the sender address, exactly as the admin write path lower-cases the stored value —
///     so a strongly-enrolled address is not missed because the two differ only in letter case.
/// </remarks>
internal interface IEmailUserLookup
{
    /// <summary>
    ///     Finds the user the normalized <paramref name="emailAddress" /> resolves to in
    ///     <paramref name="tenantId" />, or <c>null</c> when the tenant has no verified-identifier
    ///     directory, no binding exists for the address, or the binding is dangling (user removed).
    /// </summary>
    Task<EmailCallerRecord?> FindByEmailAddressAsync(string tenantId, string emailAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The bound user behind an e-mail address plus the STORED enrollment-trust dimension of its
///     verified-identifier binding (AB#5125). Mapped to a <see cref="VerifiedPrincipal" /> by
///     <see cref="EmailVerifiedCallerDirectory" />, whose <see cref="SubjectId" /> is the user's
///     runtime id — the same value the identity service issues as the token <c>sub</c> — so an
///     owner-scoped write (<c>RtCreatedBy</c>, AB#4978) stamps the human, exactly as the HTTP bearer
///     path does.
/// </summary>
internal sealed record EmailCallerRecord(
    string SubjectId,
    string? HomeTenantId,
    string? Email,
    string? Name,
    IReadOnlyList<string> Roles,
    CallerTrustLevel EnrollmentTrust);

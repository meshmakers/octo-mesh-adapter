namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     The credential material of a <c>System.Communication/ServiceAccountConfiguration</c>, as
///     either read from the runtime repository or projected into the pipeline's
///     <see cref="Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.IGlobalConfiguration" /> by the
///     communication controller (AB#5027).
/// </summary>
/// <param name="IssuerUri">
///     OIDC authority the token is requested from. May be empty — or the unresolved deploy-time
///     token <c>{{service.authority}}</c> an older controller leaves behind — which since AB#5115
///     means "this adapter's own installation" and is resolved against the adapter's own authority
///     configuration by <see cref="ServiceAccountTokenService" />.
/// </param>
/// <param name="ClientId">Client id of the service account. The only mandatory attribute.</param>
/// <param name="ClientSecret">
///     Client secret — credential material, never log it. Empty (or an angle-bracket placeholder)
///     means the account HAS no secret and the adapter acquires its tokens via the impersonation /
///     adapter-authenticated delegation grants using its own identity instead (AB#5114).
/// </param>
/// <param name="TenantId">
///     Tenant the account acts in; empty means the tenant the adapter runs for (AB#5115).
/// </param>
public sealed record ServiceAccountCredentials(
    string IssuerUri,
    string ClientId,
    string? ClientSecret,
    string? TenantId)
{
    /// <summary>
    ///     Records synthesise a <c>ToString</c> over every member; keep the secret out of it so a
    ///     structured log placeholder can never spill it.
    /// </summary>
    public override string ToString()
    {
        return $"ServiceAccountCredentials {{ IssuerUri = {IssuerUri}, ClientId = {ClientId}, "
               + $"ClientSecret = ***, TenantId = {TenantId} }}";
    }
}

/// <summary>
///     Who a service account <b>is</b>, as the identity service answered it: the subject the issued
///     client-credentials token runs on plus the roles it carries. Read off the token itself, because
///     roles are not part of the <c>ServiceAccountConfiguration</c> entity — they are assigned to the
///     client in the identity service and only materialise as <c>role</c> claims at issuance (AB#5028).
/// </summary>
/// <param name="SubjectId">
///     <c>sub</c> claim, or <c>client_id</c> for a client-credentials token that carries no subject.
/// </param>
/// <param name="Roles">Role claims of the issued token; empty when the account has no roles.</param>
/// <param name="ExpiresAtUtc">
///     When the identity stops being valid, derived from the token's <c>exp</c>. Used as the cache TTL —
///     the identity is only as current as the token it was read from.
/// </param>
public sealed record ServiceAccountIdentity(
    string SubjectId,
    IReadOnlyList<string> Roles,
    DateTime ExpiresAtUtc);

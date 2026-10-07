namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

/// <summary>
/// How the route gate of the adapter treats <b>service tokens</b> - client-credentials tokens,
/// i.e. tokens carrying neither a <c>sub</c> nor a name-identifier claim - whose
/// <c>tenant_id</c> does not name the tenant this adapter serves (AB#5628).
/// </summary>
/// <remarks>
/// Until AB#5628 a service token skipped the tenant comparison entirely, on the assumption that a
/// client-credentials token carries no tenant. Since AB#5032 identity stamps <c>tenant_id</c> on
/// every such token, so a client of tenant A holding a role whose name a route of tenant B
/// requires could call that route. The rule is now the same as <c>TenantAuthorizationMiddleware</c>
/// in octo-common-services after AB#5077: a service token may only address the tenant it was
/// issued for, and one without a <c>tenant_id</c> is refused (fail closed).
/// <para>
/// The zero value is the enforcing one on purpose: a default-constructed configuration must never
/// be the permissive one.
/// </para>
/// </remarks>
public enum ServiceTokenEnforcementMode
{
    /// <summary>
    /// <b>Default.</b> A service token of another tenant, or one without a tenant claim, is refused
    /// with <c>403 Forbidden</c> and recorded as a warning event.
    /// </summary>
    Enforce = 0,

    /// <summary>
    /// Migration mode. The request is let through, but every access <see cref="Enforce" /> would
    /// refuse is logged and recorded as a warning event naming the client id and the token tenant -
    /// the consumer inventory an operator needs before switching an environment back to
    /// <see cref="Enforce" />.
    /// </summary>
    Warn = 1
}

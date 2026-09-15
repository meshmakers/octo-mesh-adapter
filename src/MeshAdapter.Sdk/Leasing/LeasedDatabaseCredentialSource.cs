using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Holds the database credential of the tenant this member is <b>currently</b> lent, and hands it
///     to the runtime engine for that tenant's database and for no other (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Scoped by database, on purpose.</b> A pool member opens more than the borrower's
///         database — the installation's tenant registry above all — and the borrower's datasource user
///         is authorised on exactly one of them. A source that answered for every database would
///         present the borrower's user to the registry (an authentication that fails) and, worse,
///         would be the shape in which a future edit presents it to a neighbour tenant. So the held
///         credential names its database and <see cref="TryGetCredential" /> answers only for that one.
///     </para>
///     <para>
///         <b>Process-wide, not <c>AsyncLocal</c>.</b> Same reasoning as the lease tenant itself: the
///         lease arrives on a hub callback and the executions run on other async chains, so an
///         <c>AsyncLocal</c> would be empty exactly where it is needed. What makes that safe is that
///         the field is null between leases and that a member serves one tenant at a time by
///         construction — the invariant is a property of <b>time</b> (concept §4), and this class is
///         one more thing that has to obey it.
///     </para>
///     <para>
///         🔴 <b>Never persisted.</b> The credential lives in this one field, for the length of one
///         lease, and is cleared on release. It is not written to configuration, not to disk, and — see
///         <see cref="ToString" /> — not to a log.
///     </para>
/// </remarks>
internal sealed class LeasedDatabaseCredentialSource : ITenantDatabaseCredentialSource
{
    private HeldCredential? _held;

    /// <summary>
    ///     The database this member currently holds a credential for, or <c>null</c> between leases.
    /// </summary>
    /// <remarks>
    ///     Exposed so a test can assert the resting state directly rather than inferring it. The
    ///     password has no such accessor and deliberately never gets one.
    /// </remarks>
    public string? HeldDatabaseName => Volatile.Read(ref _held)?.DatabaseName;

    /// <inheritdoc />
    public bool TryGetCredential(string databaseName, out string user, out string password)
    {
        var held = Volatile.Read(ref _held);

        if (held is null || !string.Equals(held.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
        {
            user = string.Empty;
            password = string.Empty;
            return false;
        }

        user = held.User;
        password = held.Password;
        return true;
    }

    /// <summary>
    ///     Takes the credential the lease carried. Replaces whatever was held — a member that still
    ///     held one here would be a member whose previous lease did not leave.
    /// </summary>
    public void HoldForLease(string databaseName, string user, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        Volatile.Write(ref _held, new HeldCredential(databaseName, user, password));
    }

    /// <summary>
    ///     Drops the credential. Idempotent: it runs on every release path, including ones where the
    ///     lease was never fully entered.
    /// </summary>
    public void Drop() => Volatile.Write(ref _held, null);

    /// <summary>
    ///     🔴 Names no database and no user and certainly no password. This object is a singleton in a
    ///     DI graph, which is one <c>LogDebug</c> of the service provider away from being rendered.
    /// </summary>
    public override string ToString() =>
        Volatile.Read(ref _held) is null
            ? "LeasedDatabaseCredentialSource(no lease)"
            : "LeasedDatabaseCredentialSource(holding one lease credential)";

    private sealed record HeldCredential(string DatabaseName, string User, string Password)
    {
        /// <summary>🔴 A record's generated rendering would print the password.</summary>
        public override string ToString() => "<lease database credential>";
    }
}

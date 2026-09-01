using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter;

/// <summary>
/// Interface for the Mesh ETL context
/// </summary>
public interface IMeshEtlContext : IEtlContext
{
    /// <summary>
    /// Returns the associated tenant repository
    /// </summary>
    ITenantRepository TenantRepository { get; }

    /// <summary>
    ///     Opens a session under the execution's <b>effective identity</b>: the trigger-verified
    ///     caller, else the adapter's service account, else the system context (AB#5028). Sessions
    ///     opened this way stamp <c>RtCreatedBy</c> and are subject to data-level permissions
    ///     (AB#4969) — which is what a node reading or writing tenant business data must want.
    /// </summary>
    /// <remarks>
    ///     The identity is resolved lazily, on the first call of the execution, and memoised. Many
    ///     executions never open a session at all — high-frequency event triggers among them — and
    ///     must not pay for one.
    /// </remarks>
    Task<IOctoSession> GetScopedSessionAsync();

    /// <summary>
    ///     Opens a session that <b>deliberately</b> acts as <see cref="RtSecurityContext.System" />,
    ///     bypassing data-level permissions and creator stamping.
    /// </summary>
    /// <remarks>
    ///     🔴 This is the more important of the two. Its existence is what turns "which identity does
    ///     this node use" from an accident of who last touched the call site into a decision that is
    ///     written down in the code: a node either says <see cref="GetScopedSessionAsync" /> or it says
    ///     this, and a new node has to choose. Every call site of this method carries a comment saying
    ///     what breaks if it were scoped.
    /// </remarks>
    Task<IOctoSession> GetSystemSessionAsync();

    /// <summary>
    ///     Synchronous counterpart of <see cref="GetScopedSessionAsync" />, for the call sites that
    ///     use the repository's synchronous session factory.
    /// </summary>
    /// <remarks>
    ///     Blocks while the identity is resolved on first use. That is safe here — pipeline nodes run
    ///     on plain thread-pool threads with no synchronization context — but it is the reason to
    ///     prefer the asynchronous overload wherever the call site allows it.
    /// </remarks>
    IOctoSession GetScopedSession();

    /// <summary>
    ///     Synchronous counterpart of <see cref="GetSystemSessionAsync" />. Resolves no identity, so it
    ///     blocks on nothing.
    /// </summary>
    IOctoSession GetSystemSession();
}

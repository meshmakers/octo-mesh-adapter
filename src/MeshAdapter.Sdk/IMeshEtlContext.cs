using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

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
    ///     Marker that tells the nodes this run was started by the invocation of a Construction Kit
    ///     method (AB#7462, Feature AB#6884 F3.7), or <c>null</c> for every other run (manual,
    ///     cron, event). Set by the controller's method invoker only; consumed by
    ///     <see cref="Common.GenericCreateGuard" /> to let the replacing method's own pipeline run
    ///     insert entities of a type whose method <c>replacesGenericCreate</c>.
    /// </summary>
    /// <remarks>
    ///     A per-execution side channel like <see cref="IEtlContext.CallerAccessToken" />, deliberately
    ///     <b>not</b> in <see cref="IEtlContext.Properties" />: that dictionary is shared by every run
    ///     of the pipeline and can be written by pipeline nodes, so a marker there could be forged or
    ///     leak into the next run. It does not travel over <c>ToPipelineDataEvent</c>.
    /// </remarks>
    ExecutePipelineMethodInvocation? MethodInvocation { get; }

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
    ///     Subject of the service identity the pipeline runs under (AB#7596): the client id of the
    ///     pipeline's service account, or <c>null</c> when none is configured and the execution acts as
    ///     the system context. Platform-resolved, never taken from the data. Used as the writer of a manual
    ///     archive row when the execution has no verified user caller.
    /// </summary>
    ValueTask<string?> GetServiceIdentitySubjectIdAsync() => ValueTask.FromResult<string?>(null);

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
    ///     Opens a session for the identity a data node <b>selected in its configuration</b>
    ///     (AB#5127) — the third resolution alongside the two above:
    ///     <list type="bullet">
    ///         <item><see cref="NodeExecutionIdentity.Caller" /> maps to <see cref="GetScopedSessionAsync" />.</item>
    ///         <item>
    ///             <see cref="NodeExecutionIdentity.ServiceAccount" /> opens a session as the pipeline's
    ///             effective service account with its <b>full roles even when a caller is present</b> —
    ///             the elevation. There is no equivalent among the fixed two methods, which is the whole
    ///             reason this overload exists.
    ///         </item>
    ///         <item><see cref="NodeExecutionIdentity.System" /> maps to <see cref="GetSystemSessionAsync" />.</item>
    ///     </list>
    ///     An unrecognised value resolves to <see cref="NodeExecutionIdentity.Caller" /> so a missing or
    ///     future-added identity can never silently elevate.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>AB#5128 seam.</b> <see cref="NodeExecutionIdentity.ServiceAccount" /> and
    ///     <see cref="NodeExecutionIdentity.System" /> are <b>elevations</b> and are, until AB#5128
    ///     lands, <b>ungated</b> here: any pipeline author can request them. AB#5128 adds the
    ///     deploy-time authorization / confused-deputy check that refuses an un-authorized elevation;
    ///     it hooks in at the projection / deploy path, not at this call. We only ship this to
    ///     test/0.2-dev, so the capability may precede the gate for now.
    /// </remarks>
    /// <param name="identity">The identity the node's configuration selected.</param>
    Task<IOctoSession> GetSessionForAsync(NodeExecutionIdentity identity);

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

using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Writes the execution's <b>verified caller</b> (AB#5136) into the data context as a plain object
/// — <c>subjectId</c> / <c>tenantId</c> / <c>name</c> / <c>email</c> / <c>roles</c> — so the caller
/// identity can travel as <b>data</b> where the execution <i>principal</i> cannot.
/// <para>
/// The caller is resolved by a channel trigger's <c>CallerBinding</c> (Signal/e-mail/Teams) or by
/// <c>FromHttpRequest@2</c>'s bearer, but it lives only on the execution. A shared "answerer"
/// pipeline reached via <c>ToPipelineDataEvent@1</c> deliberately runs under its own service
/// identity (AB#5045 — a pipeline must not act as a caller the target never authenticated), so the
/// principal is not forwarded. This node lets the stager copy the caller into the event payload so
/// the answerer can address the user and answer "who am I" — as display information only, never to
/// act on the caller's behalf; data access stays scoped to the answerer's service account.
/// </para>
/// Writes nothing when the execution has no verified caller (anonymous / service-account run).
/// </summary>
[NodeName("WriteVerifiedCaller", 1)]
public record WriteVerifiedCallerNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Initializes the node with the default sink <c>$.caller</c>.</summary>
    public WriteVerifiedCallerNodeConfiguration()
    {
        // Default sink; a stager typically points this into the outbound event payload
        // (e.g. $.qInput.caller) so it rides along with ToPipelineDataEvent@1.
        TargetPath = "$.caller";
    }
}

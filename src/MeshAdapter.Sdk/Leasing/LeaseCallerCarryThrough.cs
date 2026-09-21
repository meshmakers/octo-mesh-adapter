using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Carries the invoker a lease names onto the execution options of the leased run (AB#5279),
///     the way <see cref="ExecuteCommandCallerCarryThrough" /> carries the invoker of an execute
///     command on a dedicated adapter. Same mapping, same three-state <see cref="CallerBindingMode" />
///     enforcement — the rule is read from the pipeline's own <c>FromExecutePipelineCommand</c>
///     trigger, because that is the trigger whose invoker this is. A pipeline without one (a cron
///     pipeline reached through the lease queue, AB#5278) has no rule to apply and runs as the
///     service account, exactly as its dedicated twin would.
/// </summary>
/// <remarks>
///     Pure static for the same reason as its dedicated twin: the mapping and the enforcement are
///     unit-testable without a lease, a bus or a running pipeline.
/// </remarks>
public static class LeaseCallerCarryThrough
{
    /// <summary>
    ///     The binding rule of the pipeline's execute trigger, or <see cref="CallerBindingMode.AnonymousAllowed" />
    ///     when the pipeline has none.
    /// </summary>
    public static CallerBindingMode ResolveBindingMode(NodeDefinitionRoot? nodeDefinitionRoot)
    {
        var trigger = nodeDefinitionRoot?.Triggers?
            .OfType<FromExecutePipelineCommandNodeConfiguration>()
            .FirstOrDefault();
        return trigger?.CallerBinding ?? CallerBindingMode.AnonymousAllowed;
    }

    /// <summary>
    ///     Applies the lease's invoker to <paramref name="options" /> under the pipeline's rule and
    ///     returns the outcome. On <see cref="CallerBindingOutcome.Reject" /> nothing is written and
    ///     the work item must fail the lease instead of running as the service account — the exact
    ///     asymmetry AB#5279 is about: an execution that runs and reports success under an identity
    ///     other than the one the caller established is a silent failure.
    /// </summary>
    public static CallerBindingOutcome Apply(LeaseDto lease, NodeDefinitionRoot? nodeDefinitionRoot,
        ExecutePipelineOptions options)
    {
        var request = new ExecutePipelineRequest(lease.TenantId, lease.PipelineInput)
        {
            Caller = lease.Caller,
            CallerAccessToken = lease.CallerAccessToken
        };
        return ExecuteCommandCallerCarryThrough.Apply(request, ResolveBindingMode(nodeDefinitionRoot), options);
    }
}

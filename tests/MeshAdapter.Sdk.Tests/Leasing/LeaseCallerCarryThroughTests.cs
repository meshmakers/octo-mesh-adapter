using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#5279 — the lease's invoker reaches the execution options under the pipeline's own
///     CallerBinding rule, exactly as an execute command's invoker does on a dedicated adapter.
/// </summary>
public class LeaseCallerCarryThroughTests
{
    private static LeaseDto ALease(ExecutePipelineCaller? caller, string? token = null) => new()
    {
        TenantId = "borrower",
        PipelineRtId = "6ad562f3ff7c40ff80275b84",
        PipelineInput = "{\"x\":1}",
        Caller = caller,
        CallerAccessToken = token
    };

    private static NodeDefinitionRoot WithExecuteTrigger(CallerBindingMode mode) => new()
    {
        Triggers = [new FromExecutePipelineCommandNodeConfiguration { CallerBinding = mode }]
    };

    private static ExecutePipelineCaller ACaller() => new()
        { SubjectId = "user-42", TenantId = "borrower", Name = "User 42", Roles = ["Admin"], TrustLevel = 2 };

    [Fact]
    public void ALeaseWithAnInvoker_RunsAsThatInvoker()
    {
        var options = new ExecutePipelineOptions(DateTime.UtcNow);

        var outcome = LeaseCallerCarryThrough.Apply(ALease(ACaller(), "raw-token"),
            WithExecuteTrigger(CallerBindingMode.AnonymousAllowed), options);

        Assert.Equal(CallerBindingOutcome.UseResolvedCaller, outcome);
        Assert.NotNull(options.VerifiedPrincipal);
        Assert.Equal("user-42", options.VerifiedPrincipal!.SubjectId);
        Assert.Equal("raw-token", options.CallerAccessToken);
        Assert.Equal(CallerTrustLevel.Strong, options.CallerTrust);
    }

    [Fact]
    public void ALeaseWithAnInvokerButNoToken_RunsAsThatInvokerWithoutDelegation()
    {
        var options = new ExecutePipelineOptions(DateTime.UtcNow);

        var outcome = LeaseCallerCarryThrough.Apply(ALease(ACaller()),
            WithExecuteTrigger(CallerBindingMode.BindingRequired), options);

        Assert.Equal(CallerBindingOutcome.UseResolvedCaller, outcome);
        Assert.Equal("user-42", options.VerifiedPrincipal!.SubjectId);
        Assert.Null(options.CallerAccessToken);
    }

    /// <summary>
    ///     🔴 The asymmetry AB#5279 is about: a required binding and no invoker must be a refused
    ///     lease, never a run as the service account that then reports success.
    /// </summary>
    [Fact]
    public void ALeaseWithoutAnInvoker_IsRejectedWhenThePipelineRequiresABoundCaller()
    {
        var options = new ExecutePipelineOptions(DateTime.UtcNow);

        var outcome = LeaseCallerCarryThrough.Apply(ALease(caller: null),
            WithExecuteTrigger(CallerBindingMode.BindingRequired), options);

        Assert.Equal(CallerBindingOutcome.Reject, outcome);
        Assert.Null(options.VerifiedPrincipal);
    }

    [Fact]
    public void ALeaseWithoutAnInvoker_RunsAsTheServiceAccountWhenAnonymousIsAllowed()
    {
        var options = new ExecutePipelineOptions(DateTime.UtcNow);

        var outcome = LeaseCallerCarryThrough.Apply(ALease(caller: null),
            WithExecuteTrigger(CallerBindingMode.AnonymousAllowed), options);

        Assert.Equal(CallerBindingOutcome.RunAsServiceAccount, outcome);
        Assert.Null(options.VerifiedPrincipal);
    }

    [Fact]
    public void APipelineWithoutAnExecuteTrigger_HasNoRuleAndRunsAsTheServiceAccount()
    {
        // A pipeline without an execute trigger (e.g. a cron pipeline reached through the lease queue, AB#5278): no rule.
        var options = new ExecutePipelineOptions(DateTime.UtcNow);
        var cronOnly = new NodeDefinitionRoot { Triggers = [new FromPollingNodeConfiguration { Interval = TimeSpan.FromMinutes(5) }] };

        var outcome = LeaseCallerCarryThrough.Apply(ALease(caller: null), cronOnly, options);

        Assert.Equal(CallerBindingMode.AnonymousAllowed, LeaseCallerCarryThrough.ResolveBindingMode(cronOnly));
        Assert.Equal(CallerBindingOutcome.RunAsServiceAccount, outcome);
    }
}

using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     What a leased mesh-adapter pool member actually runs (AB#4924 §9.9 / D4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Exactly one execution entity.</b> The controller created the
///         <c>PipelineExecution</c> at enqueue and moved it <c>Queued → Running</c> when it claimed it
///         for this lease; the terminal status, the duration and <c>LeaseReleasedAt</c> are written
///         when the lease is released. This work item therefore executes <i>against</i>
///         <see cref="LeaseDto.ExecutionId" /> and <b>reports no execution start and no execution
///         end</b>. That is not an omission: <c>PipelineExecutionService.StartExecutionAsync</c>
///         inserts a new entity with a new RtId and never looks an existing one up by
///         <c>ExecutionId</c>, so a start report would produce a second entity for one piece of work —
///         two billing spans (concept §4b) and a queue history that no longer joins up. It could not
///         report one anyway: both report verbs take their tenant and adapter from the <i>adapter hub
///         connection</i>, and a pool member holds a tenant-free management channel instead. The
///         pipeline's output travels home on the release, on <see cref="LeaseWorkOutcome.OutputData" />.
///     </para>
///     <para>
///         🔴 <b>The tenant comes from the scope, never from the lease.</b> Same rule as every service
///         around the node layer since increment 3: a work item that took the tenant from its parameter
///         would bypass the isolation mechanism and every assertion about it would be a tautology. The
///         lease is checked <i>against</i> the scope instead, and a disagreement fails the lease rather
///         than picking one.
///     </para>
///     <para>
///         <b>It runs the same orchestrator path a dedicated adapter runs.</b> Register the pipeline,
///         build the ETL context through <see cref="IContextCreatorService" />, execute through
///         <see cref="IEtlDataOrchestrator" /> — the three steps
///         <c>MeshAdapterTriggerContext.StartExecutePipelineAsync</c> takes, minus the execution-report
///         bookkeeping the controller already owns for a leased execution. So the increment 3 and 6
///         isolation work stays on the execution path, and the member behaves as the borrower's own
///         adapter would.
///     </para>
/// </remarks>
internal sealed class LeasedPipelineWorkItem(
    IServiceProvider serviceProvider,
    IAdapterTenantScope tenantScope,
    IPipelineRegistryService pipelineRegistryService,
    IContextCreatorService contextCreatorService,
    IEtlDataOrchestrator etlDataOrchestrator,
    ILogger<LeasedPipelineWorkItem> logger) : IAdapterLeaseWorkItem
{
    /// <inheritdoc />
    public async Task<LeaseWorkOutcome> RunAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lease.PipelineRtId))
        {
            // A hand-driven lease (POST {tenantId}/v1/adapterPool/{id}/lease). Legal, and the honest
            // answer is "nothing to run" rather than a failure.
            return LeaseWorkOutcome.Succeeded("The lease carries no work item.");
        }

        var tenantId = tenantScope.TenantId;
        if (!string.Equals(tenantId, lease.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            // Cannot happen while AdapterPoolClient enters the lease scope before calling the work
            // item — which is exactly why a disagreement means the two views have diverged, and the
            // safe answer to "I may be serving somebody else" is never "run it anyway".
            return LeaseWorkOutcome.Failed(
                $"The lease names tenant '{lease.TenantId}' but this process is scoped to '{tenantId}'.");
        }

        if (lease.Pipeline is null)
        {
            return LeaseWorkOutcome.Failed(
                $"The lease names pipeline {lease.PipelineRtId} but carries no configuration for it, so there is "
                + "nothing to register or run.");
        }

        var registered = await RegisterAsync(tenantId, lease.Pipeline);
        if (registered is not null)
        {
            return LeaseWorkOutcome.Failed(registered);
        }

        if (!pipelineRegistryService.TryGetPipelineRegistration(tenantId, lease.Pipeline.PipelineRtEntityId,
                out var registration))
        {
            return LeaseWorkOutcome.Failed(
                $"Pipeline {lease.PipelineRtId} was registered for the lease but could not be resolved afterwards.");
        }

        // 🔴 The execution id of the entity the controller already created. Not a fresh Guid.
        if (!Guid.TryParse(lease.ExecutionId, out var pipelineExecutionId))
        {
            return LeaseWorkOutcome.Failed(
                $"The lease carries execution id '{lease.ExecutionId}', which is not a GUID; the work item must run "
                + "against the execution the controller queued and cannot invent one.");
        }

        var options = new ExecutePipelineOptions(DateTime.UtcNow)
        {
            // Carried for the nodes and the debug stream. It is NOT reported as an execution start —
            // the controller already persisted this input on the queued entity.
            InputData = lease.PipelineInput,
            TriggerType = PipelineTriggerType.Manual
        };

        logger.LogInformation(
            "Running leased pipeline {PipelineRtEntityId} for tenant '{TenantId}' as execution "
            + "'{PipelineExecutionId}' under lease '{LeaseId}'",
            registration.PipelineRtEntityId, tenantId, pipelineExecutionId, lease.LeaseId);

        var etlContext = await contextCreatorService.CreateEtlContext<IMeshEtlContext>(registration, options,
            pipelineExecutionId);

        IPipelineDebugger? debugger = null;
        if (registration.IsDebuggingEnabled)
        {
            debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
            debugger.RegisterPipelineRtEntityId(registration.PipelineRtEntityId, pipelineExecutionId);
        }

        try
        {
            // 🔴 Awaited, not fire-and-forget. A dedicated adapter detaches the run so the RabbitMQ ack
            // is not held for its duration (AB#4279); a lease is the opposite contract — the member
            // holds the tenant until the work is done, and releasing the lease while the pipeline is
            // still running would hand the process to another tenant mid-execution.
            await etlDataOrchestrator.ExecutePipelineAsync(registration.NodeDefinitionRoot, etlContext, debugger,
                ParseInput(lease.PipelineInput));

            return LeaseWorkOutcome.Succeeded(
                $"Executed pipeline {lease.PipelineRtId} as execution '{lease.ExecutionId}'.",
                ReadOutputData(etlContext));
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Leased pipeline {PipelineRtEntityId} of tenant '{TenantId}' failed in execution "
                + "'{PipelineExecutionId}'",
                registration.PipelineRtEntityId, tenantId, pipelineExecutionId);
            // Failed, not thrown: the release has to carry the reason onto the borrower's execution,
            // and an exception escaping here would be reported as a lease failure with no pipeline in it.
            return LeaseWorkOutcome.Failed(e.Message);
        }
    }

    /// <summary>
    ///     Registers the borrower's pipeline for the duration of the lease, or returns the reason it
    ///     could not be registered.
    /// </summary>
    /// <remarks>
    ///     🔴 The counterpart is <c>PipelineRegistryLeaseParticipant</c>, which drops every registration
    ///     of the tenant on leave. That participant's <c>EnterLeaseAsync</c> is deliberately a no-op
    ///     because only the work item knows <i>which</i> pipeline the lease was granted for — this is
    ///     the method that comment refers to.
    /// </remarks>
    private async Task<string?> RegisterAsync(string tenantId, PipelineConfigurationDto pipeline)
    {
        var deploymentErrors = new List<DeploymentUpdateErrorMessageDto>();
        try
        {
            if (await pipelineRegistryService.RegisterPipelinesAsync(tenantId, [pipeline], deploymentErrors))
            {
                return null;
            }
        }
        catch (Exception e)
        {
            return $"Pipeline {pipeline.PipelineRtEntityId} could not be registered for the lease: {e.Message}";
        }

        var detail = deploymentErrors.Count == 0
            ? "no detail reported"
            : string.Join("; ", deploymentErrors.Select(m => m.ErrorMessage));
        return $"Pipeline {pipeline.PipelineRtEntityId} could not be registered for the lease: {detail}";
    }

    /// <summary>
    ///     The pipeline input as the data root, exactly as <c>FromExecutePipelineCommand@1</c> parses
    ///     it: an empty object when the trigger supplied none, so a pipeline sees the same shape it
    ///     sees on a dedicated adapter.
    /// </summary>
    private static JsonNode ParseInput(string? pipelineInput)
    {
        if (string.IsNullOrWhiteSpace(pipelineInput))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(pipelineInput) ?? new JsonObject();
        }
        catch (Exception)
        {
            // A malformed input is the author's problem and shows up as a pipeline failure with the
            // node that needed it — not as a lease that died before it started. Same tolerance the
            // command path has.
            return new JsonObject();
        }
    }

    /// <summary>
    ///     What <c>SetPipelineExecutionResult@1</c> wrote, or null. Only an explicitly set result is
    ///     returned — the same rule the dedicated path applies before reporting an execution end.
    /// </summary>
    private static string? ReadOutputData(IMeshEtlContext etlContext)
    {
        return etlContext.Properties.TryGetValue(SetPipelineExecutionResultNode.ExecutionResultPropertyKey,
            out var resultValue) && resultValue is string resultString
            ? resultString
            : null;
    }
}

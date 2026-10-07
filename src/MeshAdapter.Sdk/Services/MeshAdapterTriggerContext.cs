using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

internal class MeshAdapterTriggerContext(
    IServiceProvider serviceProvider,
    string tenantId,
    OctoObjectId dataFlowRtId,
    RtEntityId pipelineRtEntityId,
    INodeContext nodeContext, IGlobalConfiguration globalConfiguration)
    : TriggerContext(tenantId, dataFlowRtId, pipelineRtEntityId, nodeContext, globalConfiguration)
{
    private readonly ILogger<MeshAdapterTriggerContext> _logger = serviceProvider.GetRequiredService<ILogger<MeshAdapterTriggerContext>>();
    private readonly IPipelineRegistryService _pipelineRegistryService = serviceProvider.GetRequiredService<IPipelineRegistryService>();
    private readonly IEtlDataOrchestrator _etlDataOrchestrator = serviceProvider.GetRequiredService<IEtlDataOrchestrator>();
    private readonly IContextCreatorService _contextCreatorService = serviceProvider.GetRequiredService<IContextCreatorService>();
    private readonly IPipelineExecutionReporter? _executionReporter = serviceProvider.GetService<IPipelineExecutionReporter>();

    /// <inheritdoc />
    public override async Task<Guid> StartExecutePipelineAsync(ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!_pipelineRegistryService.TryGetPipelineRegistration(TenantId, PipelineRtEntityId,
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                out var pipelineRegistration) || pipelineRegistration == null)
        {
            _logger.LogWarning(
                "[{TenantId}] Pipeline {PipelineRtEntityId} no longer registered (adapter may be reconfiguring), skipping execution start",
                TenantId, PipelineRtEntityId);
            throw PipelineExecutionException.PipelineNotFound(TenantId, PipelineRtEntityId);
        }


        var pipelineExecutionId = Guid.NewGuid();
        _logger.LogDebug("[{TenantId}] Running pipeline for pipeline {PipelineRtEntityId} as run with execution id {PipelineExecutionId}", TenantId,
            PipelineRtEntityId, pipelineExecutionId);

        DateTime startedDateTime = DateTime.UtcNow;

        // Report execution start to communication controller — BEFORE the execution context exists
        // (AB#5493). Mirrors the SDK host (AdapterTriggerContext). Everything the start report needs
        // is known here, and a failure in context creation (MeshContextCreatorService →
        // ISystemContext.FindTenantRepositoryAsync: "System tenant database does not exist") used
        // to throw past the reporter: the controller never saw an execution, PipelineStatistics
        // froze and octo.pipeline.execution.failures stayed at zero while the finAPI cron trigger
        // failed on every tick for four days (prod-1).
        if (_executionReporter != null)
        {
            await _executionReporter.ReportExecutionStartAsync(
                PipelineRtEntityId,
                pipelineExecutionId,
                executePipelineOptions.TriggerType,
                startedDateTime,
                executePipelineOptions.InputData);
        }

        IMeshEtlContext etlContext;
        IPipelineDebugger? debugger = null;
        IPipelineExecutionMode? executionMode;
        try
        {
            etlContext = await _contextCreatorService.CreateEtlContext<IMeshEtlContext>(pipelineRegistration, executePipelineOptions, pipelineExecutionId);

            if (pipelineRegistration.IsDebuggingEnabled)
            {
                _logger.LogWarning("[{TenantId}] Debugging enabled for pipeline {PipelineRtEntityId} with execution id {PipelineExecutionId}", TenantId,
                    PipelineRtEntityId, pipelineExecutionId);

                debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
                debugger.RegisterPipelineRtEntityId(PipelineRtEntityId, pipelineExecutionId);
            }

            // AB#5159: the dry-run flag arrives on the options (set by FromExecutePipelineCommand@1
            // from the ExecutePipelineRequest) and only reaches the nodes as an execution mode on
            // the orchestrator call. Mirrors the SDK host (AdapterTriggerContext); without it every
            // Load node saw a null mode and ran for real.
            executionMode = executePipelineOptions.IsDryRun
                ? new DefaultPipelineExecutionMode { IsDryRun = true }
                : null;

            if (executePipelineOptions.IsDryRun && debugger == null)
            {
                // Dry-run intents are written to the debug stream; without a debugger
                // the agent can't inspect them. Force-enable per-execution so the
                // would-have-written record is captured - together with every node's
                // input/output snapshot, as in any debug-enabled run. Real-effect runs
                // are unchanged - debugger stays opt-in via IsDebuggingEnabled.
                debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
                debugger.RegisterPipelineRtEntityId(PipelineRtEntityId, pipelineExecutionId);
                _logger.LogInformation(
                    "[{TenantId}] Pipeline {PipelineRtEntityId} dry-run execution {PipelineExecutionId}: forced debugger on so intent payloads are captured",
                    TenantId, PipelineRtEntityId, pipelineExecutionId);
            }
        }
        catch (Exception ex)
        {
            // The start is already on record, so this execution has to be closed as Failed here:
            // no task is registered yet, so EndExecutePipelineAsync never runs for this id and the
            // end report cannot be doubled. Rethrown so the trigger sees the failure as before.
            await ReportExecutionFailedBeforeRunAsync(pipelineExecutionId, startedDateTime, ex);
            throw;
        }

        Task<object?> task = Task.Run(async () =>
        {
            var r = await _etlDataOrchestrator.ExecutePipelineAsync(
                pipelineRegistration.NodeDefinitionRoot,
                etlContext, debugger, value, executionMode);

            return r;
        });
        var execution = pipelineRegistration.RegisterExecution(pipelineExecutionId, startedDateTime, task);
        execution.Properties["EtlContext"] = etlContext;

        return pipelineExecutionId;
    }

    /// <summary>
    /// Closes an execution whose start was reported but which never reached the orchestrator
    /// (AB#5493): logs the failure and reports it as <see cref="PipelineExecutionStatus.Failed" />
    /// under the same execution id, so the controller's statistics and the failure metric see it.
    /// Mirrors the SDK host (AdapterTriggerContext).
    /// </summary>
    private async Task ReportExecutionFailedBeforeRunAsync(Guid pipelineExecutionId, DateTime startedDateTime,
        Exception ex)
    {
        _logger.LogError(ex,
            "[{TenantId}] Pipeline {PipelineRtEntityId} execution {PipelineExecutionId} failed before the pipeline ran: the execution context could not be created",
            TenantId, PipelineRtEntityId, pipelineExecutionId);

        if (_executionReporter == null)
        {
            return;
        }

        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedDateTime).TotalMilliseconds;
        await _executionReporter.ReportExecutionEndAsync(
            pipelineExecutionId,
            PipelineExecutionStatus.Failed,
            completedAt,
            durationMs,
            ex.Message);
    }

    /// <inheritdoc />
    public override async Task ReportStatusAsync(string message, bool isError = false,
        CancellationToken cancellationToken = default)
    {
        // AB#5385: beside the execution reports, through the same reporter — it already owns the
        // hub client and the never-throw contract. Mirrors the SDK host (AdapterTriggerContext);
        // without a reporter the line has nowhere to go and is dropped silently, exactly like the
        // execution reports.
        if (_executionReporter == null)
        {
            return;
        }

        try
        {
            await _executionReporter.ReportPipelineStatusAsync(PipelineRtEntityId, message, isError, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // The reporter swallows on its own; this is the belt to its braces, because a status
            // line must never take a poll loop down.
            _logger.LogDebug(ex, "[{TenantId}] Reporting the status line of pipeline {PipelineRtEntityId} failed",
                TenantId, PipelineRtEntityId);
        }
    }

    /// <inheritdoc />
    public override async Task<object?> EndExecutePipelineAsync(Guid pipelineExecutionId)
    {
        if (!_pipelineRegistryService.TryGetPipelineRegistration(TenantId, PipelineRtEntityId,
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                out var pipelineRegistration) || pipelineRegistration == null)
        {
            _logger.LogWarning(
                "[{TenantId}] Pipeline {PipelineRtEntityId} no longer registered (adapter may be reconfiguring), skipping execution end for {PipelineExecutionId}",
                TenantId, PipelineRtEntityId, pipelineExecutionId);
            return null;
        }

        var startedAt = pipelineRegistration.GetExecutionStartTime(pipelineExecutionId) ?? DateTime.UtcNow;

        // Retrieve the EtlContext stored during StartExecutePipelineAsync so we can
        // read any OutputData that a SetPipelineExecutionResult node wrote to it.
        var etlContext = pipelineRegistration.GetExecutionPropertyValue<IMeshEtlContext>(pipelineExecutionId, "EtlContext");

        var status = PipelineExecutionStatus.Running;
        string? errorMessage = null;
        object? result = null;

        try
        {
            result = await pipelineRegistration.UnregisterExecutionAsync(pipelineExecutionId);
            status = PipelineExecutionStatus.Completed;
        }
        catch (Exception ex)
        {
            status = PipelineExecutionStatus.Failed;
            errorMessage = ex.Message;
            _logger.LogError(ex, "[{TenantId}] Pipeline execution failed for pipeline {PipelineRtEntityId}",
                TenantId, PipelineRtEntityId);
            throw;
        }
        finally
        {
            // Capture completion time AFTER the pipeline task has been awaited
            var completedAt = DateTime.UtcNow;
            var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

            // Report execution end to communication controller
            if (_executionReporter != null)
            {
                // Only include OutputData if explicitly set by SetPipelineExecutionResult node
                string? outputData = null;
                if (etlContext?.Properties.TryGetValue(
                        SetPipelineExecutionResultNode.ExecutionResultPropertyKey, out var resultValue) == true
                    && resultValue is string resultString)
                {
                    outputData = resultString;
                }

                await _executionReporter.ReportExecutionEndAsync(
                    pipelineExecutionId,
                    status,
                    completedAt,
                    durationMs,
                    errorMessage,
                    outputData);
            }

            _logger.LogDebug("[{TenantId}] Pipeline finished for pipeline {PipelineRtEntityId} with status {Status}",
                TenantId, PipelineRtEntityId, status);
        }

        return result;
    }
}
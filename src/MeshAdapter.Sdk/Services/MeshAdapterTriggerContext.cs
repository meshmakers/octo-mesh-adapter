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
        var etlContext = await _contextCreatorService.CreateEtlContext<IMeshEtlContext>(pipelineRegistration, executePipelineOptions, pipelineExecutionId);

        IPipelineDebugger? debugger = null;
        if (pipelineRegistration.IsDebuggingEnabled)
        {
            _logger.LogWarning("[{TenantId}] Debugging enabled for pipeline {PipelineRtEntityId} with execution id {PipelineExecutionId}", TenantId,
                PipelineRtEntityId, pipelineExecutionId);

            debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
            debugger.RegisterPipelineRtEntityId(PipelineRtEntityId, pipelineExecutionId);
        }

        DateTime startedDateTime = DateTime.UtcNow;

        // Report execution start to communication controller
        if (_executionReporter != null)
        {
            await _executionReporter.ReportExecutionStartAsync(
                PipelineRtEntityId,
                pipelineExecutionId,
                executePipelineOptions.TriggerType,
                startedDateTime,
                executePipelineOptions.InputData);
        }

        // AB#5159: the dry-run flag arrives on the options (set by FromExecutePipelineCommand@1
        // from the ExecutePipelineRequest) and only reaches the nodes as an execution mode on
        // the orchestrator call. Mirrors the SDK host (AdapterTriggerContext); without it every
        // Load node saw a null mode and ran for real.
        IPipelineExecutionMode? executionMode = executePipelineOptions.IsDryRun
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
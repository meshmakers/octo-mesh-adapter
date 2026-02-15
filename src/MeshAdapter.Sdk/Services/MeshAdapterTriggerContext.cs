using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

internal class MeshAdapterTriggerContext(
    IServiceProvider serviceProvider,
    string tenantId,
    OctoObjectId dataPipelineRtId,
    RtEntityId pipelineRtEntityId,
    INodeContext nodeContext, IGlobalConfiguration globalConfiguration)
    : TriggerContext(tenantId, dataPipelineRtId, pipelineRtEntityId, nodeContext, globalConfiguration)
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

        Task<object?> task = Task.Run(async () =>
        {
            var r = await _etlDataOrchestrator.ExecutePipelineAsync(
                pipelineRegistration.NodeDefinitionRoot,
                etlContext, debugger, value);

            return r;
        });
        pipelineRegistration.RegisterExecution(pipelineExecutionId, startedDateTime, task);

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
        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

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
        }
        finally
        {
            // Report execution end to communication controller
            if (_executionReporter != null)
            {
                await _executionReporter.ReportExecutionEndAsync(
                    pipelineExecutionId,
                    status,
                    completedAt,
                    durationMs,
                    errorMessage);
            }

            _logger.LogDebug("[{TenantId}] Pipeline finished for pipeline {PipelineRtEntityId} with status {Status}",
                TenantId, PipelineRtEntityId, status);
        }

        return result;
    }
}
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

internal class MeshAdapterTriggerContext(
    IServiceProvider serviceProvider,
    string tenantId,
    OctoObjectId dataPipelineRtId,
    RtEntityId pipelineRtEntityId,
    INodeContext nodeContext) : TriggerContext(tenantId, dataPipelineRtId, pipelineRtEntityId, nodeContext)
{
    private readonly ILogger<MeshAdapterTriggerContext> _logger = serviceProvider.GetRequiredService<ILogger<MeshAdapterTriggerContext>>();
    private readonly IPipelineRegistryService _pipelineRegistryService = serviceProvider.GetRequiredService<IPipelineRegistryService>();
    private readonly IEtlDataOrchestrator _etlDataOrchestrator = serviceProvider.GetRequiredService<IEtlDataOrchestrator>();
    private readonly IContextCreatorService _contextCreatorService = serviceProvider.GetRequiredService<IContextCreatorService>();

    /// <inheritdoc />
    public override async Task<Guid> StartExecutePipelineAsync(ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!_pipelineRegistryService.TryGetPipelineRegistration(TenantId, PipelineRtEntityId,
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                out var pipelineRegistration) || pipelineRegistration == null)
        {
            _logger.LogError("[{TenantId}] Pipeline {Id} not found", PipelineRtEntityId, TenantId);
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
            _logger.LogError("[{TenantId}] Pipeline {Id} not found", PipelineRtEntityId, TenantId);
            throw PipelineExecutionException.PipelineNotFound(TenantId, PipelineRtEntityId);
        }
        
        var result  = await pipelineRegistration.UnregisterExecutionAsync(pipelineExecutionId);
        _logger.LogDebug("[{TenantId}] Pipeline finished for pipeline {PipelineRtEntityId}", TenantId,
            PipelineRtEntityId);
        return result;
    }
}
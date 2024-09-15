using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// Interface for the Mesh pipeline execution service
/// </summary>
public interface IMeshPipelineExecutionService : IPipelineExecutionService
{
    /// <summary>
    /// Execute a pipeline by data pipeline runtime id
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="dataPipelineRtId"></param>
    /// <param name="executePipelineOptions"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    Task ExecutePipelineByDataPipelineRtId(string tenantId, OctoObjectId dataPipelineRtId,
        ExecutePipelineOptions executePipelineOptions, object? value = null);
}

/// <summary>
/// Mesh pipeline execution service
/// </summary>
/// <param name="logger"></param>
/// <param name="etlDataOrchestrator"></param>
/// <param name="systemContext"></param>
/// <param name="pipelineConfigurationSerializer"></param>
public class MeshPipelineExecutionService(
    IServiceProvider serviceProvider,
    ILogger<MeshPipelineExecutionService> logger,
    IEtlDataOrchestrator etlDataOrchestrator,
    ISystemContext systemContext,
    IPipelineConfigurationSerializer pipelineConfigurationSerializer)
    : PipelineExecutionService(pipelineConfigurationSerializer), IMeshPipelineExecutionService
{
    public override async Task<Guid> StartExecutePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId,
        ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineRegistrationsById.TryGetValue(CreateByIdKey(tenantId, pipelineRtEntityId),
                out var pipelineRegistration))
        {
            logger.LogError("[{TenantId}] Pipeline {Id} not found", pipelineRtEntityId, tenantId);
            throw PipelineExecutionException.PipelineNotFound(tenantId, pipelineRtEntityId);
        }

        string message = "";
        if (value is string stringValue)
        {
            message = stringValue;
        }
        
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();

        var pipelineExecutionId = Guid.NewGuid();
        logger.LogDebug("[{TenantId}] Running pipeline for pipeline {PipelineRtEntityId} as run with execution id {PipelineExecutionId}", tenantId,
            pipelineRtEntityId, pipelineExecutionId);
        var retrieverEtlContext = new MeshEtlContext(pipelineRegistration.TenantId, message, tenantRepository,
            session, pipelineRegistration.DataPipelineRtId, pipelineExecutionId, pipelineRegistration.PipelineRtEntityId,
            executePipelineOptions.TransactionStartedDateTime,
            executePipelineOptions.ExternalReceivedDateTime, pipelineRegistration.Dictionary);

        IPipelineDebugger? debugger = null;
        if (pipelineRegistration.IsDebuggingEnabled)
        {
            debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
            debugger.RegisterPipelineRtEntityId(pipelineRtEntityId, pipelineExecutionId);
        }
        
        session.StartTransaction();
        DateTime startedDateTime = DateTime.UtcNow;

        Task<object?> task = Task.Run(async () =>
        {
            var r = await etlDataOrchestrator.ExecutePipelineAsync<IMeshEtlContext>(
                pipelineRegistration.ConfigurationRoot,
                retrieverEtlContext, debugger);

            try
            {
                logger.LogDebug("[{TenantId}] Committing transaction for pipeline {PipelineRtEntityId} as run with execution id {PipelineExecutionId}", tenantId,
                    pipelineRtEntityId, pipelineExecutionId);
                await session.CommitTransactionAsync();
                session.Dispose();
                logger.LogDebug("[{TenantId}] Transaction committed for pipeline {PipelineRtEntityId} as run with execution id {PipelineExecutionId}", tenantId,
                    pipelineRtEntityId, pipelineExecutionId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "[{TenantId}] Error while committing transaction for pipeline {PipelineRtEntityId} as run with execution id {PipelineExecutionId}", tenantId,
                    pipelineRtEntityId, pipelineExecutionId);
                throw;
            }

            return r;
        });
        pipelineRegistration.RegisterExecution(pipelineExecutionId, startedDateTime, task);

        return pipelineExecutionId;
    }

    public override async Task<object?> EndExecutePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId)
    {
        if (!PipelineRegistrationsById.TryGetValue(CreateByIdKey(tenantId, pipelineRtEntityId),
                out var pipelineRegistration))
        {
            logger.LogError("[{TenantId}] Pipeline {Id} not found", pipelineRtEntityId, tenantId);
            throw PipelineExecutionException.PipelineNotFound(tenantId, pipelineRtEntityId);
        }
        
        var result = await pipelineRegistration.UnregisterExecutionAsync(pipelineExecutionId);
        logger.LogDebug("[{TenantId}] Pipeline finished for pipeline {PipelineRtEntityId}", tenantId,
            pipelineRtEntityId);
        
        return result;
    }

    public async Task ExecutePipelineByDataPipelineRtId(string tenantId, OctoObjectId dataPipelineRtId,
        ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineRegistrationsByDataPipelineId.TryGetValue(CreateDataPipelineIdKey(tenantId, dataPipelineRtId),
                out var pipelineExecutionItems))
        {
            logger.LogError("[{TenantId}] No Pipelines for data pipeline {DataPipelineRtId} found",
                dataPipelineRtId, tenantId);
            return;
        }

        foreach (var pipelineExecutionItem in pipelineExecutionItems)
        {
            await ExecutePipelineAsync(pipelineExecutionItem.TenantId, pipelineExecutionItem.PipelineRtEntityId,
                executePipelineOptions, value);
        }
    }
}
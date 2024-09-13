using Meshmakers.Octo.ConstructionKit.Contracts;
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

    /// <inheritdoc />
    public override async Task<object?> ExecutePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId,
        ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineExecutionItemsById.TryGetValue(CreateByIdKey(tenantId, pipelineRtEntityId),
                out var pipelineExecutionItem))
        {
            logger.LogError("[{TenantId}] Pipeline {Id} not found", pipelineRtEntityId, tenantId);
            throw PipelineExecutionException.PipelineNotFound(tenantId, pipelineRtEntityId);
        }

        string message = "";
        if (value is string stringValue)
        {
            message = stringValue;
        }

        try
        {

            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            logger.LogDebug("[{TenantId}] Running pipeline for pipeline {PipelineRtEntityId}", tenantId,
                pipelineRtEntityId);
            var retrieverEtlContext = new MeshEtlContext(pipelineExecutionItem.TenantId, message, tenantRepository,
                session, pipelineExecutionItem.DataPipelineRtId, pipelineExecutionItem.PipelineRtEntityId,
                executePipelineOptions.TransactionStartedDateTime,
                executePipelineOptions.ExternalReceivedDateTime, pipelineExecutionItem.Dictionary);

            IPipelineDebugger? debugger = null;
            if (pipelineExecutionItem.IsDebuggingEnabled)
            {
                debugger = serviceProvider.GetRequiredService<IPipelineDebugger>();
                debugger.RegisterPipelineRtEntityId(pipelineRtEntityId);
            }

            var r = await etlDataOrchestrator.ExecutePipelineAsync<IMeshEtlContext>(pipelineExecutionItem.ConfigurationRoot,
                retrieverEtlContext, debugger);
            
            await session.CommitTransactionAsync();

            logger.LogDebug("[{TenantId}] Pipeline finished for pipeline {PipelineRtEntityId}", tenantId,
                pipelineRtEntityId);

            return r;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[{TenantId}] Failed to execute pipeline {PipelineRtEntityId}", tenantId,
                pipelineRtEntityId);
            throw PipelineExecutionException.PipelineExecutionFailed(pipelineExecutionItem.TenantId,
                pipelineExecutionItem.DataPipelineRtId, pipelineExecutionItem.PipelineRtEntityId, e);
        }
    }

    public async Task ExecutePipelineByDataPipelineRtId(string tenantId, OctoObjectId dataPipelineRtId,
        ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineExecutionItemsByDataPipelineId.TryGetValue(CreateDataPipelineIdKey(tenantId, dataPipelineRtId),
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
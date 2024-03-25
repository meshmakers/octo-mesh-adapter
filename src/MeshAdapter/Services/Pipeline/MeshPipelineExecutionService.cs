using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
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
    Task ExecutePipelineByDataPipelineRtId(string tenantId, OctoObjectId dataPipelineRtId, ExecutePipelineOptions executePipelineOptions, object? value = null);
}

/// <summary>
/// Mesh pipeline execution service
/// </summary>
/// <param name="logger"></param>
/// <param name="etlDataOrchestrator"></param>
/// <param name="systemContext"></param>
/// <param name="pipelineConfigurationSerializer"></param>
public class MeshPipelineExecutionService(ILogger<MeshPipelineExecutionService> logger,
    IEtlDataOrchestrator etlDataOrchestrator,ISystemContext systemContext,
    IPipelineConfigurationSerializer pipelineConfigurationSerializer)
    : PipelineExecutionService(pipelineConfigurationSerializer), IMeshPipelineExecutionService
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);


    /// <inheritdoc />
    public override async Task ExecutePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId, ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineExecutionItemsById.TryGetValue(CreateByIdKey(tenantId, pipelineRtEntityId), out var pipelineExecutionItem))
        {
            logger.LogError("Pipeline {Id} not found in tenant {TenantId}", pipelineRtEntityId, tenantId);
            return;
        }

        if (!(value is string message))
        {
            logger.LogError("Message is not a string, skipping");
            return;
        }

        try
        {
            await _semaphore.WaitAsync();

            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            logger.LogDebug("Running pipeline for tenant {TenantId} and pipeline {PipelineRtEntityId}", tenantId,
                pipelineRtEntityId);
            var retrieverEtlContext = new MeshEtlContext(pipelineExecutionItem.TenantId, message, tenantRepository,
                session, pipelineExecutionItem.DataPipelineRtId, pipelineExecutionItem.PipelineRtEntityId, executePipelineOptions.TransactionStartedDateTime,
                executePipelineOptions.ExternalReceivedDateTime, pipelineExecutionItem.Dictionary);
            await etlDataOrchestrator.ExecutePipelineAsync<IMeshEtlContext>(pipelineExecutionItem.ConfigurationRoot,
                retrieverEtlContext);
            logger.LogDebug("Pipeline finished for tenant {TenantId} and pipeline {PipelineRtEntityId}", tenantId,
                pipelineRtEntityId);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "[{TenantId}] Failed to execute pipeline {PipelineRtEntityId}", tenantId, pipelineRtEntityId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExecutePipelineByDataPipelineRtId(string tenantId, OctoObjectId dataPipelineRtId,
        ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineExecutionItemsByDataPipelineId.TryGetValue(CreateDataPipelineIdKey(tenantId, dataPipelineRtId), out var pipelineExecutionItems))
        {
            logger.LogError("No Pipelines for data pipeline {DataPipelineRtId} found in tenant {TenantId}", dataPipelineRtId, tenantId);
            return;
        }
        
        foreach (var pipelineExecutionItem in pipelineExecutionItems)
        {
            await ExecutePipelineAsync(pipelineExecutionItem.TenantId, pipelineExecutionItem.PipelineRtEntityId, executePipelineOptions, value);
        }
    }
}
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.Services;
using NLog;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// Interface for the Sender pipeline execution service
/// </summary>
public interface ISenderPipelineExecutionService : IPipelineExecutionService;

/// <summary>
/// Sender pipeline execution service
/// </summary>
/// <param name="logger"></param>
/// <param name="etlDataOrchestrator"></param>
/// <param name="systemContext"></param>
/// <param name="pipelineConfigurationSerializer"></param>
public class SenderPipelineExecutionService(ILogger<RetrieverPipelineExecutionService> logger,
    IEtlDataOrchestrator etlDataOrchestrator,ISystemContext systemContext,
    IPipelineConfigurationSerializer pipelineConfigurationSerializer)
    : PipelineExecutionService(pipelineConfigurationSerializer), ISenderPipelineExecutionService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly SemaphoreSlim _semaphore = new(1, 1);


    /// <inheritdoc />
    public override async Task ExecutePipelineAsync(string tenantId, OctoObjectId pipelineRtId, ExecutePipelineOptions executePipelineOptions, object? value = null)
    {
        if (!PipelineExecutionItems.TryGetValue(CreateKey(tenantId, pipelineRtId), out var pipelineExecutionItem))
        {
            Logger.Error("Pipeline {Id} not found in tenant '{TenantId}'", pipelineRtId, tenantId);
            return;
        }

        if (!(value is string message))
        {
            Logger.Error("Message is not a string, skipping");
            return;
        }

        try
        {
            await _semaphore.WaitAsync();

            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            logger.LogDebug("Running pipeline for tenant '{TenantId}' and pipeline '{PipelineId}'", tenantId,
                pipelineRtId);
            var retrieverEtlContext = new RetrieverEtlContext(pipelineExecutionItem.TenantId, message, tenantRepository,
                session, pipelineRtId, executePipelineOptions.TransactionStartedDateTime,
                executePipelineOptions.ExternalReceivedDateTime, pipelineExecutionItem.Dictionary);
            await etlDataOrchestrator.ExecutePipelineAsync<IRetrieverEtlContext>(pipelineExecutionItem.ConfigurationRoot, retrieverEtlContext);
            logger.LogDebug("Pipeline finished for tenant '{TenantId}' and pipeline '{PipelineId}'", tenantId,
                pipelineRtId);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "[{TenantId}] Failed to execute pipeline '{DataPipelineRtId}'", tenantId, pipelineRtId);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
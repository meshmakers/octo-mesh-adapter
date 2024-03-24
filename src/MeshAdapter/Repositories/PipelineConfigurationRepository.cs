using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.MeshAdapter.Repositories;

/// <summary>
/// Repository for pipeline configuration
/// </summary>
public class PipelineConfigurationRepository : IPipelineConfigurationRepository
{
    private readonly ISystemContext _systemContext;
    
    private const string RetrieverPipelineConfigurationAttribute = "RetrieverPipelineConfiguration";
    private const string SenderPipelineConfigurationAttribute = "SenderPipelineConfiguration";
    private static readonly CkId<CkTypeId> DataPipelineCkTypeId = new("System.Communication/Pipeline");

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="systemContext"></param>
    public PipelineConfigurationRepository(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }


    private static RtEntityId GetPipelineRtEntityId(OctoObjectId pipelineRtId) =>
        new(DataPipelineCkTypeId, pipelineRtId);

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineConfigurationDto>> GetRetrieverConfigurationsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataQueryOperation = DataQueryOperation.Create();
        var pipeline = await tenantRepository.GetRtEntitiesByTypeAsync(session, DataPipelineCkTypeId, dataQueryOperation);
        await session.CommitTransactionAsync();

        return pipeline.Items.Where(x=> !string.IsNullOrWhiteSpace(x.GetAttributeStringValueOrDefault(RetrieverPipelineConfigurationAttribute))).Select(p =>
            new PipelineConfigurationDto(p.RtId, false,
                p.GetAttributeStringValue(RetrieverPipelineConfigurationAttribute)));
    }
    
    /// <inheritdoc />
    public async Task<IEnumerable<PipelineConfigurationDto>> GetSenderConfigurationsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataQueryOperation = DataQueryOperation.Create();
        var pipeline = await tenantRepository.GetRtEntitiesByTypeAsync(session, DataPipelineCkTypeId, dataQueryOperation);
        await session.CommitTransactionAsync();

        return pipeline.Items.Where(x=> !string.IsNullOrWhiteSpace(x.GetAttributeStringValueOrDefault(SenderPipelineConfigurationAttribute))).Select(p =>
            new PipelineConfigurationDto(p.RtId, false,
                p.GetAttributeStringValue(SenderPipelineConfigurationAttribute)));
    }

    /// <inheritdoc />
    public async Task<PipelineConfigurationDto> GetRetrieverConfigurationAsync(string tenantId,
        OctoObjectId pipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var pipeline = await tenantRepository.GetRtEntityByRtIdAsync(session, GetPipelineRtEntityId(pipelineRtId));
        await session.CommitTransactionAsync();
        if (pipeline == null)
        {
            throw MeshAdapterException.PipelineConfigurationNotFound(tenantId, pipelineRtId);
        }

        var configString = pipeline.GetAttributeStringValueOrDefault(RetrieverPipelineConfigurationAttribute);
        if (string.IsNullOrWhiteSpace(configString))
        {
            throw MeshAdapterException.PipelineConfigurationNotFound(tenantId, pipelineRtId);
        }

        return new PipelineConfigurationDto(pipelineRtId, false,
            configString);
    }
    
    /// <inheritdoc />
    public async Task<PipelineConfigurationDto> GetSenderConfigurationAsync(string tenantId,
        OctoObjectId pipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var pipeline = await tenantRepository.GetRtEntityByRtIdAsync(session, GetPipelineRtEntityId(pipelineRtId));
        await session.CommitTransactionAsync();
        if (pipeline == null)
        {
            throw MeshAdapterException.PipelineConfigurationNotFound(tenantId, pipelineRtId);
        }

        var configString = pipeline.GetAttributeStringValueOrDefault(SenderPipelineConfigurationAttribute);
        if (string.IsNullOrWhiteSpace(configString))
        {
            throw MeshAdapterException.PipelineConfigurationNotFound(tenantId, pipelineRtId);
        }

        return new PipelineConfigurationDto(pipelineRtId, false,
            configString);
    }
}
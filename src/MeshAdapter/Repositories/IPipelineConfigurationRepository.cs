using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.MeshAdapter.Repositories;

/// <summary>
/// Repository for pipeline configuration
/// </summary>
public interface IPipelineConfigurationRepository
{
    /// <summary>
    /// Get the retriever pipeline configuration of a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IEnumerable<PipelineConfigurationDto>> GetRetrieverConfigurationsAsync(string tenantId);

    /// <summary>
    /// Get the sender pipeline configuration of a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IEnumerable<PipelineConfigurationDto>> GetSenderConfigurationsAsync(string tenantId);

    /// <summary>
    /// Get the retriever configuration of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Runtime id of pipeline</param>
    /// <returns></returns>
    Task<PipelineConfigurationDto> GetRetrieverConfigurationAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    /// Get the sender configuration of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Runtime id of pipeline</param>
    /// <returns></returns>
    Task<PipelineConfigurationDto> GetSenderConfigurationAsync(string tenantId, OctoObjectId pipelineRtId);
}
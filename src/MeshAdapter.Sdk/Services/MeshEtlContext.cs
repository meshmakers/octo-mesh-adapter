using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// ETL context for the mesh adapter
/// </summary>
public class MeshEtlContext : DefaultEtlContext, IMeshEtlContext
{
    /// <summary>
    /// Create a new instance of <see cref="MeshEtlContext"/>
    /// </summary>
    /// <param name="tenantRepository">Tenant repository</param>
    /// <param name="adapterReceivedDateTime">Received date time from the adapter</param>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Data flow runtime identifier</param>
    /// <param name="pipelineExecutionId">Guid that identifies the pipeline execution instance</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="externalReceivedDateTime">Date and time when the value was received by an optional external system</param>
    /// <param name="globalConfiguration">Global configuration for the pipeline</param>
    /// <param name="properties">properties that are shared between the different stages of the ETL process and different runs of the pipeline</param>
    public MeshEtlContext(string tenantId, ITenantRepository tenantRepository,
        OctoObjectId dataFlowRtId, Guid pipelineExecutionId, RtEntityId pipelineRtEntityId, DateTime adapterReceivedDateTime, DateTime? externalReceivedDateTime,
        IGlobalConfiguration globalConfiguration, IDictionary<string, object?> properties)
        : base(tenantId, dataFlowRtId, pipelineExecutionId, pipelineRtEntityId, adapterReceivedDateTime, externalReceivedDateTime, globalConfiguration, properties)
    {
        TenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public ITenantRepository TenantRepository { get; }
}
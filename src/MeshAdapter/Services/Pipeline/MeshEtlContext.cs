using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// ETL context for the mesh adapter
/// </summary>
public class MeshEtlContext : DefaultEtlContext, IMeshEtlContext
{
    /// <summary>
    /// Create a new instance of <see cref="MeshEtlContext"/>
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="message"></param>
    /// <param name="tenantRepository"></param>
    /// <param name="session"></param>
    /// <param name="dataPipelineRtId"></param>
    /// <param name="pipelineRtEntityId"></param>
    /// <param name="externalReceivedDateTime"></param>
    /// <param name="properties"></param>
    /// <param name="adapterReceivedDateTime"></param>
    public MeshEtlContext(string tenantId, string message, ITenantRepository tenantRepository,
        IOctoSession session, OctoObjectId dataPipelineRtId,  RtEntityId pipelineRtEntityId, DateTime adapterReceivedDateTime, DateTime? externalReceivedDateTime,
        IDictionary<string, object?> properties)
        : base(tenantId, dataPipelineRtId, pipelineRtEntityId, adapterReceivedDateTime, externalReceivedDateTime, properties)
    {
        Message = message;
        TenantRepository = tenantRepository;
        Session = session;
    }

    /// <inheritdoc />
    public string Message { get; }

    /// <inheritdoc />
    public ITenantRepository TenantRepository { get; }

    /// <inheritdoc />
    public IOctoSession Session { get; }
}
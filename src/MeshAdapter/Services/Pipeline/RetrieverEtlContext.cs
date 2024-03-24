using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// ETL context for the Retriever
/// </summary>
public class RetrieverEtlContext : DefaultEtlContext, IRetrieverEtlContext
{
    /// <summary>
    /// Create a new instance of <see cref="RetrieverEtlContext"/>
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="message"></param>
    /// <param name="tenantRepository"></param>
    /// <param name="session"></param>
    /// <param name="dataPipelineRtId"></param>
    /// <param name="externalReceivedDateTime"></param>
    /// <param name="properties"></param>
    /// <param name="adapterReceivedDateTime"></param>
    public RetrieverEtlContext(string tenantId, string message, ITenantRepository tenantRepository,
        IOctoSession session, OctoObjectId dataPipelineRtId, DateTime adapterReceivedDateTime, DateTime? externalReceivedDateTime,
        IDictionary<string, object?> properties)
        : base(tenantId, dataPipelineRtId, adapterReceivedDateTime, externalReceivedDateTime, properties)
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
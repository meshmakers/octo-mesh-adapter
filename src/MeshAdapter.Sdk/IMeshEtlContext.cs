using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter;

/// <summary>
/// Interface for the Mesh ETL context
/// </summary>
public interface IMeshEtlContext : IEtlContext
{
    /// <summary>
    /// Returns the associated tenant repository
    /// </summary>
    ITenantRepository TenantRepository { get; }
}
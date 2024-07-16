using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// Interface for the Mesh ETL context
/// </summary>
public interface IMeshEtlContext : IEtlContext
{
    /// <summary>
    /// Returns the message
    /// </summary>
    string Message { get; }
    
    /// <summary>
    /// Returns the associated tenant repository
    /// </summary>
    ITenantRepository TenantRepository { get; }

    /// <summary>
    /// Returns the current session
    /// </summary>
    IOctoSession Session { get; }
}
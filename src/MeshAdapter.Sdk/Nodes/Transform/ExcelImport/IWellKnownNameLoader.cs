using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

/// <summary>
/// Loads well-known names from the repository.
/// </summary>
public interface IWellKnownNameLoader
{
    /// <summary>
    /// Loads well-known names from the repository based on the provided names and type ID.
    /// </summary>
    /// <param name="wellKnownNames">List of well-known names to load.</param>
    /// <param name="ckTypeId">The type ID of the Construction Kit entity to filter by.</param>
    /// <returns>A task that represents the asynchronous operation, containing a collection of <see cref="RtEntity"/>.</returns>
    Task<IDictionary<string, RtEntity>> LoadAsync(
        IEnumerable<string> wellKnownNames,
        CkId<CkTypeId> ckTypeId);
}
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

/// <summary>
/// Loads well-known names from the repository.
/// </summary>
/// <param name="etlContext">The ETL context to use for loading.</param>
internal class WellKnownNameLoader(IMeshEtlContext etlContext) : IWellKnownNameLoader
{
    public async Task<IDictionary<string, RtEntity>> LoadAsync(
        IEnumerable<string> wellKnownNames,
        RtCkId<CkTypeId> rtCkTypeId)
    {
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldIn(nameof(RtEntity.RtWellKnownName), wellKnownNames);

        using var session = etlContext.TenantRepository.GetSession();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session,
            rtCkTypeId, queryOptions);

        await session.CommitTransactionAsync();

        var result = new Dictionary<string, RtEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var rtEntity in r.Items)
        {
            if (rtEntity.RtWellKnownName != null)
            {
                result[rtEntity.RtWellKnownName.Trim().ToLower()] = rtEntity;
            }
        }

        return result;
    }
}
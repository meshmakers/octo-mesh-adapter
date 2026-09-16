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

        // AB#5028 — SYSTEM by decision, and SYNCHRONOUS (the second of only two such call sites):
        // this is ImportFromExcel@1's lookup of the entities the import links against, so it must
        // resolve the same set the import writes. Scoped, an existing entity the identity cannot see
        // would be re-created as a duplicate instead of being reused.
        using var session = etlContext.GetSystemSession();
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
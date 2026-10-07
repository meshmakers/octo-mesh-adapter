using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// <see cref="IConfigurationSecretAttributeResolver" /> backed by the CK cache (AB#5538): the names of
/// every <c>Secret</c> attribute of a configuration CK type, top level and record members (single
/// records and record arrays) at any depth. An unknown type answers <c>null</c>, so the caller falls
/// back to the known credential names.
/// </summary>
internal sealed class CkConfigurationSecretAttributeResolver(ICkCacheService ckCacheService)
    : IConfigurationSecretAttributeResolver
{
    /// <inheritdoc />
    public IReadOnlyCollection<string>? GetSecretAttributeNames(string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        if (!ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var graph))
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(tenantId, graph, names, depth: 0);
        return names;
    }

    private void Collect(string tenantId, CkTypeWithAttributesGraph graph, HashSet<string> names, int depth)
    {
        // Records nest through the model; the guard only protects against a malformed cache.
        if (depth > 16)
        {
            return;
        }

        foreach (var attribute in graph.AllAttributesByName.Values)
        {
            switch (attribute.ValueType)
            {
                case AttributeValueTypesDto.Secret:
                    names.Add(attribute.AttributeName);
                    break;
                case AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray
                    when attribute.ValueCkRecordId != null &&
                         ckCacheService.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var record):
                    Collect(tenantId, record, names, depth + 1);
                    break;
            }
        }
    }
}

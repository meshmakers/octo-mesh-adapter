using System.Collections;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// CK-type-aware handling of <c>Secret</c> attributes in the mesh adapter (AB#5538, concept AB#5528).
/// </summary>
/// <remarks>
/// <para>
/// The engine serialisers write an <see cref="RtSecretValue" /> only as the marker
/// <c>{"isSet": …}</c>, so an entity read through a normalised repository path never puts a plaintext
/// or an envelope into the data context. Two places are not normalised and must be decided by the CK
/// attribute type instead of by the CLR type of the value:
/// </para>
/// <list type="bullet">
/// <item><description>MongoDB <b>change streams</b> (<c>FromWatchRtEntity@1</c>): a string in a Secret
/// slot — clear text stored before the attribute became Secret, or an <c>enc:v1</c> value — arrives
/// as a plain <see cref="string" />. <see cref="MaskLegacyValues" /> wraps it as
/// <see cref="RtSecretValue.LegacyPlaintext" />, which serialises as the marker.</description></item>
/// <item><description><b>Write-back</b> through the data context (<c>CreateUpdateInfo@1</c> →
/// <c>ApplyChanges@2</c>): a pending secret serialised into the data context would arrive as
/// "unchanged", so the plaintext has to travel as a plain string (see
/// <see cref="IsSecretAttribute" />).</description></item>
/// </list>
/// </remarks>
public static class SecretAttributes
{
    /// <summary>
    /// Finds an attribute of a CK type or record by name, case-insensitively (pipeline authors write
    /// camelCase, the CK model PascalCase).
    /// </summary>
    public static CkTypeAttributeGraph? FindAttribute(CkTypeWithAttributesGraph graph, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.AllAttributesByName.TryGetValue(attributeName, out var exact))
        {
            return exact;
        }

        return graph.AllAttributesByName.Values.FirstOrDefault(a =>
            string.Equals(a.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when <paramref name="attributeName" /> is a top-level attribute of value type
    /// <c>Secret</c> on <paramref name="ckTypeId" />. Unknown types or attributes answer false.
    /// </summary>
    public static bool IsSecretAttribute(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId>? ckTypeId, string attributeName)
    {
        if (ckTypeId == null || !ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var graph))
        {
            return false;
        }

        return FindAttribute(graph, attributeName)?.ValueType == AttributeValueTypesDto.Secret;
    }

    /// <summary>
    /// Replaces every plain string found in a Secret slot of <paramref name="entity" /> — top level and
    /// inside records, at any depth — with <see cref="RtSecretValue.LegacyPlaintext" />, so the entity
    /// serialises the slot as the marker. Values that already are <see cref="RtSecretValue" />s are left
    /// alone. An entity whose CK type is not in the cache is left unchanged.
    /// </summary>
    /// <returns>The number of values wrapped</returns>
    public static int MaskLegacyValues(ICkCacheService ckCacheService, string tenantId, RtEntity? entity)
    {
        if (entity?.CkTypeId == null || !ckCacheService.TryGetRtCkType(tenantId, entity.CkTypeId, out var graph))
        {
            return 0;
        }

        return MaskAttributes(ckCacheService, tenantId, graph, entity, depth: 0);
    }

    private static int MaskAttributes(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph graph, RtTypeWithAttributes target, int depth)
    {
        // Records nest through the model, not through data; the guard only protects against a
        // malformed cache with a cycle.
        if (depth > 16)
        {
            return 0;
        }

        var masked = 0;
        foreach (var (name, value) in target.Attributes.ToList())
        {
            if (value == null)
            {
                continue;
            }

            var attribute = FindAttribute(graph, name);
            if (attribute == null)
            {
                continue;
            }

            switch (attribute.ValueType)
            {
                case AttributeValueTypesDto.Secret when value is string text:
                    target.SetAttributeRawValue(name, RtSecretValue.LegacyPlaintext(text));
                    masked++;
                    break;
                case AttributeValueTypesDto.Record when value is RtRecord record:
                    masked += MaskRecord(ckCacheService, tenantId, record, depth);
                    break;
                case AttributeValueTypesDto.RecordArray when value is IEnumerable items and not string:
                    foreach (var item in items)
                    {
                        if (item is RtRecord element)
                        {
                            masked += MaskRecord(ckCacheService, tenantId, element, depth);
                        }
                    }

                    break;
            }
        }

        return masked;
    }

    private static int MaskRecord(ICkCacheService ckCacheService, string tenantId, RtRecord record, int depth)
    {
        if (record.CkRecordId == null ||
            !ckCacheService.TryGetRtCkRecord(tenantId, record.CkRecordId, out var recordGraph))
        {
            return 0;
        }

        return MaskAttributes(ckCacheService, tenantId, recordGraph, record, depth + 1);
    }
}

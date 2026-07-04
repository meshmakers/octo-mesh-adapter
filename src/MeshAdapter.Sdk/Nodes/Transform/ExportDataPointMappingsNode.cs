using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Serialises the tenant's DataPointMapping entities into a portable export
/// document (<see cref="DataPointMappingExportDocument"/>). Each mapping's
/// source/target endpoint is resolved via its MapsFrom/MapsTo association and
/// exported with natural keys: the configured identity attribute per CK type
/// (e.g. LoxoneUuid) plus the entity name — so the document can be re-imported
/// after a tenant re-initialisation, where all RtIds have changed.
/// </summary>
[NodeConfiguration(typeof(ExportDataPointMappingsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class ExportDataPointMappingsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ExportDataPointMappingsNodeConfiguration>();
        var mappingCkTypeId = new RtCkId<CkTypeId>(c.MappingCkTypeId);
        var mapsFromRoleId = new RtCkId<CkAssociationRoleId>(c.MapsFromRoleId);
        var mapsToRoleId = new RtCkId<CkAssociationRoleId>(c.MapsToRoleId);
        var excludeRegex = string.IsNullOrWhiteSpace(c.ExcludeNameRegex)
            ? null
            : new Regex(c.ExcludeNameRegex);
        var identityByType = c.IdentityAttributes
            .Where(i => !string.IsNullOrWhiteSpace(i.CkTypeId) && !string.IsNullOrWhiteSpace(i.Attribute))
            .ToDictionary(i => i.CkTypeId, i => i.Attribute, StringComparer.Ordinal);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var mappings = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, mappingCkTypeId, RtEntityQueryOptions.Create())).Items.ToList();

        // Endpoint entities are shared across mappings (many mappings → one
        // target) — cache them per (ckTypeId, rtId) to avoid repeated loads.
        var entityCache = new Dictionary<string, RtEntity?>(StringComparer.Ordinal);
        var exported = new List<ExportedMapping>();
        var skippedByRegex = 0;
        var skippedDisabled = 0;

        foreach (var mapping in mappings)
        {
            var name = mapping.GetAttributeValueOrDefault("Name") as string ?? string.Empty;
            var enabled = mapping.GetAttributeValueOrDefault<bool>("Enabled") ?? true;

            if (!c.IncludeDisabled && !enabled)
            {
                skippedDisabled++;
                continue;
            }

            if (excludeRegex != null && excludeRegex.IsMatch(name))
            {
                skippedByRegex++;
                continue;
            }

            var source = await ResolveEndpointAsync(session, mapping, mapsFromRoleId, identityByType, entityCache);
            var target = await ResolveEndpointAsync(session, mapping, mapsToRoleId, identityByType, entityCache);

            exported.Add(new ExportedMapping(
                name,
                enabled,
                mapping.GetAttributeValueOrDefault("SourceAttributePath") as string ?? string.Empty,
                mapping.GetAttributeValueOrDefault("TargetAttributePath") as string ?? string.Empty,
                mapping.GetAttributeValueOrDefault("MappingExpression") as string ?? string.Empty,
                source,
                target));
        }

        var document = new DataPointMappingExportDocument(1, c.MappingCkTypeId, exported);
        dataContext.Set(c.TargetPath, document, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        nodeContext.Info(
            $"Exported {exported.Count} of {mappings.Count} mappings " +
            $"({skippedByRegex} excluded by name regex, {skippedDisabled} disabled skipped).");

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Resolves one endpoint of a mapping (via the given outbound role) into a
    /// portable reference. Returns null when the mapping has no association of
    /// that role — the entry is still exported so the import can report it.
    /// </summary>
    private async Task<ExportedEntityRef?> ResolveEndpointAsync(
        IOctoSession session,
        RtEntity mapping,
        RtCkId<CkAssociationRoleId> roleId,
        IReadOnlyDictionary<string, string> identityByType,
        Dictionary<string, RtEntity?> entityCache)
    {
        var assocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
            session,
            new RtEntityId(mapping.CkTypeId!, mapping.RtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, roleId));

        var assoc = assocs.Items.FirstOrDefault();
        if (assoc == null)
        {
            return null;
        }

        var ckTypeId = assoc.TargetCkTypeId;
        var ckTypeIdStr = ckTypeId.ToString();
        var cacheKey = $"{ckTypeIdStr}@{assoc.TargetRtId}";
        if (!entityCache.TryGetValue(cacheKey, out var entity))
        {
            var load = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                session, ckTypeId, new[] { assoc.TargetRtId }, RtEntityQueryOptions.Create());
            entity = load.Items.FirstOrDefault();
            entityCache[cacheKey] = entity;
        }

        string? entityName = null;
        string? identityAttribute = null;
        string? identityValue = null;
        if (entity != null)
        {
            entityName = entity.GetAttributeValueOrDefault("Name") as string;
            if (identityByType.TryGetValue(ckTypeIdStr, out var configuredAttribute))
            {
                var raw = entity.GetAttributeValueOrDefault(configuredAttribute);
                identityValue = raw as string ?? raw?.ToString();
                if (!string.IsNullOrWhiteSpace(identityValue))
                {
                    identityAttribute = configuredAttribute;
                }
                else
                {
                    identityValue = null;
                }
            }
        }

        return new ExportedEntityRef(
            ckTypeIdStr,
            assoc.TargetRtId.ToString(),
            entityName,
            identityAttribute,
            identityValue);
    }
}

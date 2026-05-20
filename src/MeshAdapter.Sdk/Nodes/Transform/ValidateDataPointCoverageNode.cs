using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Walks an entity hierarchy and reports DataPointMapping coverage per node against
/// a set of <see cref="CoverageRule"/> profiles. Output format at <c>TargetPath</c>:
/// <code>
/// {
///   "treeRtId": "...",
///   "treeCkTypeId": "...",
///   "generatedAt": "2026-05-19T12:34:56Z",
///   "summary": { "ok": 12, "warning": 3, "error": 2, "info": 0, "total": 17 },
///   "nodes": [
///     { "rtId": "...", "ckTypeId": "EnergyIQ/Space", "name": "Room 12.34",
///       "status": "error|warning|ok|info",
///       "required": ["Temperature","CO2Level","Humidity"],
///       "recommended": ["SetpointTemperature"],
///       "present":  ["Temperature","CO2Level"],
///       "missingRequired": ["Humidity"],
///       "missingRecommended": ["SetpointTemperature"],
///       "mappingCount": 2,
///       "depth": 3 }
///   ]
/// }
/// </code>
/// </summary>
[NodeConfiguration(typeof(ValidateDataPointCoverageNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class ValidateDataPointCoverageNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ValidateDataPointCoverageNodeConfiguration>();

        var rootRtId = ResolveRootRtId(dataContext, c, nodeContext);
        if (rootRtId == null)
        {
            nodeContext.Warning("ValidateDataPointCoverage: root rtId could not be resolved, skipping");
            await next(dataContext, nodeContext);
            return;
        }

        var rootCkTypeId = new RtCkId<CkTypeId>(c.RootCkTypeId);
        // `c.ChildCkTypeId` is retained on the config for documentation purposes
        // — it tells the operator which CK type the role is *intended* to link.
        // We no longer pass it to the engine filter because polymorphism doesn't
        // work there (see comment in the BFS loop below).
        var childRoleId = new RtCkId<CkAssociationRoleId>(c.ChildRoleId);
        var mappingRoleId = new RtCkId<CkAssociationRoleId>(c.MappingRoleId);
        var mappingCkTypeId = new RtCkId<CkTypeId>(c.MappingCkTypeId);

        var rulesByCkType = c.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.CkTypeId))
            .ToDictionary(r => r.CkTypeId, r => r, StringComparer.OrdinalIgnoreCase);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Load root entity
        var rootResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
            session, rootCkTypeId, new[] { rootRtId.Value },
            RtEntityQueryOptions.Create());
        var rootEntity = rootResult.Items.FirstOrDefault();
        if (rootEntity == null)
        {
            nodeContext.Warning("ValidateDataPointCoverage: root entity {0}@{1} not found",
                c.RootCkTypeId, rootRtId.Value);
            await next(dataContext, nodeContext);
            return;
        }

        var nodes = new List<NodeReport>();
        var summary = new SummaryCounters();

        // BFS over the hierarchy (root + children via inbound ParentChild).
        // Tuple carries the entity's ck type explicitly so we never depend on the
        // (nullable) CkTypeId property that the engine model marks as optional.
        var queue = new Queue<(RtEntity entity, RtCkId<CkTypeId> ckTypeId, int depth)>();
        queue.Enqueue((rootEntity, rootCkTypeId, 0));
        var visited = new HashSet<OctoObjectId> { rootEntity.RtId };

        while (queue.Count > 0)
        {
            var (current, currentCkTypeId, depth) = queue.Dequeue();

            var nodeReport = await BuildReportAsync(
                session, current, currentCkTypeId, depth, rulesByCkType,
                mappingRoleId, mappingCkTypeId, c.IncludeDisabledMappings);
            nodes.Add(nodeReport);
            summary.Increment(nodeReport.Status);

            if (depth >= c.MaxDepth) continue;

            // Inbound ParentChild → children of `current`.
            //
            // We deliberately do NOT pass `targetTypeId: childCkTypeId` here:
            // the MongoDB engine's relatedRtCkTypeId filter is strict and does
            // not match derived types. A Site's child Buildings declare their
            // concrete ckType (EnergyIQ/Building, not Basic/TreeNode), so a
            // strict filter on Basic/TreeNode returns zero rows. We accept any
            // origin type and read its concrete ckTypeId from the association,
            // then load each entity batch under its own type.
            var childAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
                session,
                new RtEntityId(currentCkTypeId, current.RtId),
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, childRoleId));

            // Group child ids by their concrete origin ckTypeId — we need the
            // right type to look the entity up in its MongoDB collection.
            var childrenByType = new Dictionary<RtCkId<CkTypeId>, List<OctoObjectId>>();
            foreach (var assoc in childAssocs.Items)
            {
                if (assoc.OriginCkTypeId == null) continue;
                if (!visited.Add(assoc.OriginRtId)) continue;
                if (!childrenByType.TryGetValue(assoc.OriginCkTypeId, out var bucket))
                {
                    bucket = new List<OctoObjectId>();
                    childrenByType[assoc.OriginCkTypeId] = bucket;
                }
                bucket.Add(assoc.OriginRtId);
            }

            if (childrenByType.Count == 0) continue;

            foreach (var (ckType, ids) in childrenByType)
            {
                var childrenResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                    session, ckType, ids, RtEntityQueryOptions.Create());
                foreach (var child in childrenResult.Items)
                {
                    queue.Enqueue((child, ckType, depth + 1));
                }
            }
        }

        var report = new JObject
        {
            ["treeRtId"] = rootEntity.RtId.ToString(),
            ["treeCkTypeId"] = rootCkTypeId.ToString(),
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["summary"] = new JObject
            {
                ["ok"] = summary.Ok,
                ["warning"] = summary.Warning,
                ["error"] = summary.Error,
                ["info"] = summary.Info,
                ["total"] = nodes.Count,
            },
            ["nodes"] = new JArray(nodes.Select(SerialiseNode)),
        };

        dataContext.SetValueByPath(c.TargetPath, report, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        nodeContext.Info(
            "ValidateDataPointCoverage: validated {0} nodes — ok={1} warning={2} error={3} info={4}",
            nodes.Count, summary.Ok, summary.Warning, summary.Error, summary.Info);

        await next(dataContext, nodeContext);
    }

    private static OctoObjectId? ResolveRootRtId(IDataContext dataContext,
        ValidateDataPointCoverageNodeConfiguration c, INodeContext nodeContext)
    {
        if (!string.IsNullOrWhiteSpace(c.RootRtId))
        {
            try
            {
                return new OctoObjectId(c.RootRtId);
            }
            catch (Exception ex)
            {
                nodeContext.Warning("ValidateDataPointCoverage: invalid RootRtId '{0}': {1}",
                    c.RootRtId, ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(c.RootRtIdPath))
        {
            var value = dataContext.GetSimpleValueByPath<string>(c.RootRtIdPath);
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    return new OctoObjectId(value);
                }
                catch (Exception ex)
                {
                    nodeContext.Warning(
                        "ValidateDataPointCoverage: invalid rtId '{0}' at path '{1}': {2}",
                        value, c.RootRtIdPath, ex.Message);
                }
            }
        }

        return null;
    }

    private async Task<NodeReport> BuildReportAsync(
        IOctoSession session,
        RtEntity entity,
        RtCkId<CkTypeId> entityCkTypeId,
        int depth,
        IReadOnlyDictionary<string, CoverageRule> rulesByCkType,
        RtCkId<CkAssociationRoleId> mappingRoleId,
        RtCkId<CkTypeId> mappingCkTypeId,
        bool includeDisabledMappings)
    {
        var name = entity.GetAttributeValueOrDefault("Name") as string
                   ?? entity.GetAttributeValueOrDefault("name") as string
                   ?? entity.RtId.ToString();

        // Find inbound MapsTo associations → DataPointMapping entities.
        var mappingAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
            session,
            new RtEntityId(entityCkTypeId, entity.RtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, mappingRoleId,
                targetTypeId: mappingCkTypeId));

        var mappingIds = mappingAssocs.Items.Select(a => a.OriginRtId).Distinct().ToList();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int mappingCount = 0;

        if (mappingIds.Count > 0)
        {
            var mappingsResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                session, mappingCkTypeId, mappingIds, RtEntityQueryOptions.Create());

            foreach (var mapping in mappingsResult.Items)
            {
                var enabled = mapping.GetAttributeValueOrDefault<bool>("Enabled") ?? true;
                if (!enabled && !includeDisabledMappings) continue;
                mappingCount++;

                var targetPath = mapping.GetAttributeValueOrDefault("TargetAttributePath") as string;
                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    present.Add(targetPath.Trim());
                }
            }
        }

        // Resolve which rule applies — exact CK type id match.
        rulesByCkType.TryGetValue(entityCkTypeId.ToString(), out var rule);
        var evaluation = EvaluateCoverage(rule, present);

        return new NodeReport(
            entity.RtId.ToString(),
            entityCkTypeId.ToString(),
            name,
            evaluation.Status,
            depth,
            evaluation.Required,
            evaluation.Recommended,
            present.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            evaluation.MissingRequired,
            evaluation.MissingRecommended,
            mappingCount);
    }

    /// <summary>
    /// Pure-function coverage evaluator. Given the rule for an entity's CK type and the
    /// set of target attribute paths currently mapped onto it, computes the coverage
    /// status, plus the lists of required/recommended attributes (split into present and
    /// missing).
    /// </summary>
    /// <remarks>Status rules:
    /// <list type="bullet">
    /// <item><c>info</c> — no rule for this CK type.</item>
    /// <item><c>error</c> — at least one required attribute missing.</item>
    /// <item><c>warning</c> — required complete, at least one recommended missing.</item>
    /// <item><c>ok</c> — all required and recommended attributes present.</item>
    /// </list>
    /// </remarks>
    internal static CoverageEvaluation EvaluateCoverage(CoverageRule? rule, ISet<string> present)
    {
        if (rule == null)
        {
            return new CoverageEvaluation(
                Status: "info",
                Required: Array.Empty<string>(),
                Recommended: Array.Empty<string>(),
                MissingRequired: Array.Empty<string>(),
                MissingRecommended: Array.Empty<string>());
        }

        var required = rule.RequiredAttributes ?? new List<string>();
        var recommended = rule.RecommendedAttributes ?? new List<string>();
        var missingRequired = required.Where(a => !present.Contains(a)).ToList();
        var missingRecommended = recommended.Where(a => !present.Contains(a)).ToList();

        string status;
        if (missingRequired.Count > 0) status = "error";
        else if (missingRecommended.Count > 0) status = "warning";
        else status = "ok";

        return new CoverageEvaluation(
            Status: status,
            Required: required,
            Recommended: recommended,
            MissingRequired: missingRequired,
            MissingRecommended: missingRecommended);
    }

    internal record CoverageEvaluation(
        string Status,
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Recommended,
        IReadOnlyList<string> MissingRequired,
        IReadOnlyList<string> MissingRecommended);

    private static JObject SerialiseNode(NodeReport r)
    {
        return new JObject
        {
            ["rtId"] = r.RtId,
            ["ckTypeId"] = r.CkTypeId,
            ["name"] = r.Name,
            ["status"] = r.Status,
            ["depth"] = r.Depth,
            ["required"] = JArray.FromObject(r.Required),
            ["recommended"] = JArray.FromObject(r.Recommended),
            ["present"] = JArray.FromObject(r.Present),
            ["missingRequired"] = JArray.FromObject(r.MissingRequired),
            ["missingRecommended"] = JArray.FromObject(r.MissingRecommended),
            ["mappingCount"] = r.MappingCount,
        };
    }

    private record NodeReport(
        string RtId,
        string CkTypeId,
        string Name,
        string Status,
        int Depth,
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Recommended,
        IReadOnlyList<string> Present,
        IReadOnlyList<string> MissingRequired,
        IReadOnlyList<string> MissingRecommended,
        int MappingCount);

    private sealed class SummaryCounters
    {
        public int Ok;
        public int Warning;
        public int Error;
        public int Info;

        public void Increment(string status)
        {
            switch (status)
            {
                case "ok": Ok++; break;
                case "warning": Warning++; break;
                case "error": Error++; break;
                default: Info++; break;
            }
        }
    }
}

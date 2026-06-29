using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
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
/// Deterministically generates DataPointMapping suggestion records from declarative rules.
/// The output array shape matches AnthropicAiQueryNode so the same downstream ForEach
/// (GetOrCreate + CreateUpdateInfo + CreateAssociationUpdate) consumes both.
/// </summary>
[NodeConfiguration(typeof(GenerateDataPointMappingsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class GenerateDataPointMappingsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GenerateDataPointMappingsNodeConfiguration>();

        var containerCkTypeId = new RtCkId<CkTypeId>(c.SourceContainerCkTypeId);
        var controlCkTypeId = new RtCkId<CkTypeId>(c.SourceControlCkTypeId);
        var targetCkTypeId = new RtCkId<CkTypeId>(c.TargetCkTypeId);
        var categoryCkTypeId = string.IsNullOrWhiteSpace(c.SourceCategoryCkTypeId)
            ? (RtCkId<CkTypeId>?)null
            : new RtCkId<CkTypeId>(c.SourceCategoryCkTypeId);
        var hierarchyRoleId = new RtCkId<CkAssociationRoleId>(c.HierarchyAssociationRoleId);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var containers = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, containerCkTypeId, RtEntityQueryOptions.Create())).Items.ToList();
        var targets = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, targetCkTypeId, RtEntityQueryOptions.Create())).Items.ToList();

        nodeContext.Info(
            $"Loaded {containers.Count} containers ({c.SourceContainerCkTypeId}), {targets.Count} targets ({c.TargetCkTypeId})");

        var matcher = new ContainerMatcher(c.ContainerMatchingStrategies, targets);

        var suggestions = new List<MappingSuggestion>();
        var matched = 0;
        var unmatched = new List<string>();
        var rulesById = c.ControlMappingRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToDictionary(r => r.Id!, r => r, StringComparer.Ordinal);
        var rulesHitByRuleId = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            var containerName = container.GetAttributeValueOrDefault("Name") as string ?? "(unnamed)";
            var target = matcher.Match(container);
            if (target == null)
            {
                unmatched.Add(containerName);
                continue;
            }

            matched++;

            var controls = await LoadControlsAsync(
                session, container, controlCkTypeId, categoryCkTypeId, hierarchyRoleId);

            // Per-container cache of child-target resolutions (e.g. Space → TemperatureSensor).
            // Cleared on every container so different rooms resolve to different sensors.
            var childTargetCache = new Dictionary<string, RtEntity?>(StringComparer.Ordinal);

            foreach (var (control, category) in controls)
            {
                foreach (var rule in c.ControlMappingRules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Map.TargetAttribute))
                    {
                        continue;
                    }

                    // Resolve the actual emission target. v1 rules emit against the matched
                    // container target; v2-style rules with ChildTargetCkTypeId resolve a
                    // child entity (e.g. the TemperatureSensor inside the Space).
                    var actualTarget = target;
                    var actualTargetCkTypeId = c.TargetCkTypeId;

                    if (!string.IsNullOrWhiteSpace(rule.Map.ChildTargetCkTypeId))
                    {
                        var childKey = rule.Map.ChildTargetCkTypeId!;
                        if (!childTargetCache.TryGetValue(childKey, out var resolved))
                        {
                            resolved = await ResolveChildTargetAsync(
                                session, target,
                                childKey,
                                rule.Map.ChildTargetAssociationRoleId ?? c.HierarchyAssociationRoleId);
                            childTargetCache[childKey] = resolved;
                        }

                        if (resolved == null)
                        {
                            // No child of that type in this container — rule does not apply.
                            continue;
                        }

                        actualTarget = resolved;
                        actualTargetCkTypeId = childKey;
                    }

                    var emitted = TryEmitRule(rule, control, category, actualTarget, actualTargetCkTypeId,
                        containerName, c, suggestions);
                    if (emitted > 0)
                    {
                        rulesHitByRuleId[rule.Id!] = rulesHitByRuleId.GetValueOrDefault(rule.Id!) + emitted;
                    }
                }
            }
        }

        // Direct (non-container) mappings: building-/area-level data points wired control-name → entity.
        await ProcessDirectMappingsAsync(session, c, controlCkTypeId, suggestions, rulesHitByRuleId, nodeContext);

        nodeContext.Info(
            $"Matched {matched}/{containers.Count} containers, produced {suggestions.Count} mapping suggestions " +
            $"across {rulesHitByRuleId.Count} rule(s).");
        if (unmatched.Count > 0)
        {
            nodeContext.Warning(
                $"{unmatched.Count} container(s) had no matching target: " +
                string.Join(", ", unmatched.Take(20)) + (unmatched.Count > 20 ? ", …" : ""));
        }

        dataContext.Set(c.TargetPath, suggestions, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        if (!string.IsNullOrWhiteSpace(c.StatisticsTargetPath))
        {
            var statistics = new MappingStatistics(
                containers.Count,
                matched,
                unmatched.Count,
                unmatched,
                suggestions.Count,
                rulesHitByRuleId,
                rulesById.Keys
                    .Concat(c.DirectControlMappings.Where(d => !string.IsNullOrWhiteSpace(d.Id)).Select(d => d.Id!))
                    .Distinct(StringComparer.Ordinal).ToList());
            dataContext.Set(c.StatisticsTargetPath, statistics, c.DocumentMode, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Loads all controls reachable from a container via the configured hierarchy role.
    /// When a Category type is configured, controls are discovered via Container → Category → Control;
    /// otherwise via Container → Control directly. Returned tuples carry the category (if any) so
    /// rules can match on category attributes.
    /// </summary>
    private async Task<List<(RtEntity Control, RtEntity? Category)>> LoadControlsAsync(
        IOctoSession session,
        RtEntity container,
        RtCkId<CkTypeId> controlCkTypeId,
        RtCkId<CkTypeId>? categoryCkTypeId,
        RtCkId<CkAssociationRoleId> hierarchyRoleId)
    {
        var result = new List<(RtEntity Control, RtEntity? Category)>();

        if (categoryCkTypeId != null)
        {
            var categoryAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
                session,
                new RtEntityId(container.CkTypeId!, container.RtId),
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, hierarchyRoleId,
                    targetTypeId: categoryCkTypeId));

            foreach (var catAssoc in categoryAssocs.Items)
            {
                var catLoad = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                    session, categoryCkTypeId, new[] { catAssoc.OriginRtId },
                    RtEntityQueryOptions.Create());
                var category = catLoad.Items.FirstOrDefault();
                if (category == null) continue;

                var controlAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
                    session,
                    new RtEntityId(category.CkTypeId!, category.RtId),
                    RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, hierarchyRoleId,
                        targetTypeId: controlCkTypeId));

                foreach (var ctrlAssoc in controlAssocs.Items)
                {
                    var ctrlLoad = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                        session, controlCkTypeId, new[] { ctrlAssoc.OriginRtId },
                        RtEntityQueryOptions.Create());
                    var control = ctrlLoad.Items.FirstOrDefault();
                    if (control != null) result.Add((control, (RtEntity?)category));
                }
            }
        }
        else
        {
            var controlAssocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
                session,
                new RtEntityId(container.CkTypeId!, container.RtId),
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, hierarchyRoleId,
                    targetTypeId: controlCkTypeId));

            foreach (var ctrlAssoc in controlAssocs.Items)
            {
                var ctrlLoad = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                    session, controlCkTypeId, new[] { ctrlAssoc.OriginRtId },
                    RtEntityQueryOptions.Create());
                var control = ctrlLoad.Items.FirstOrDefault();
                if (control != null) result.Add((control, (RtEntity?)null));
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a single child entity reachable from the matched container target via the
    /// given association role (inbound from the target). Used by v2-style rules where the
    /// real mapping target lives one association hop deeper than the matched container
    /// (e.g. EnergyIQ/Space contains a TemperatureSensor via the SpaceSensors role).
    ///
    /// Returns the first child entity of the configured type, or null when no such child
    /// exists. If multiple children of the same type exist, the first one returned by the
    /// repository wins — for richer disambiguation use additional rule filters or extend
    /// the schema.
    /// </summary>
    private async Task<RtEntity?> ResolveChildTargetAsync(
        IOctoSession session,
        RtEntity container,
        string childCkTypeIdStr,
        string roleIdStr)
    {
        var childCkTypeId = new RtCkId<CkTypeId>(childCkTypeIdStr);
        var roleId = new RtCkId<CkAssociationRoleId>(roleIdStr);

        var assocs = await etlContext.TenantRepository.GetRtAssociationsAsync(
            session,
            new RtEntityId(container.CkTypeId!, container.RtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, roleId,
                targetTypeId: childCkTypeId));

        var first = assocs.Items.FirstOrDefault();
        if (first == null) return null;

        var load = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
            session, childCkTypeId, new[] { first.OriginRtId },
            RtEntityQueryOptions.Create());
        return load.Items.FirstOrDefault();
    }

    /// <summary>
    /// Processes the direct (non-container) control→entity mappings. Loads all source controls once
    /// (tenant-wide), resolves each mapping's target entity by name/rtId, and emits one suggestion per
    /// state the control exposes. Appends to <paramref name="suggestions"/> and records per-id hit
    /// counts in <paramref name="rulesHitByRuleId"/>.
    /// </summary>
    private async Task ProcessDirectMappingsAsync(
        IOctoSession session,
        GenerateDataPointMappingsNodeConfiguration c,
        RtCkId<CkTypeId> controlCkTypeId,
        List<MappingSuggestion> suggestions,
        Dictionary<string, int> rulesHitByRuleId,
        INodeContext nodeContext)
    {
        if (c.DirectControlMappings.Count == 0) return;

        // All source controls, tenant-wide (direct mappings are not container-scoped).
        var allControls = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, controlCkTypeId, RtEntityQueryOptions.Create())).Items.ToList();

        // Cache target-entity lists per CkTypeId (each mapping may target a different type).
        var targetsByType = new Dictionary<string, List<RtEntity>>(StringComparer.Ordinal);

        foreach (var dm in c.DirectControlMappings)
        {
            if (string.IsNullOrWhiteSpace(dm.Id) || string.IsNullOrWhiteSpace(dm.TargetCkTypeId) ||
                dm.States.Count == 0)
            {
                continue;
            }

            if (!targetsByType.TryGetValue(dm.TargetCkTypeId!, out var typeTargets))
            {
                typeTargets = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
                    session, new RtCkId<CkTypeId>(dm.TargetCkTypeId!),
                    RtEntityQueryOptions.Create())).Items.ToList();
                targetsByType[dm.TargetCkTypeId!] = typeTargets;
            }

            var target = ResolveDirectTarget(dm, typeTargets);
            if (target == null)
            {
                nodeContext.Warning(
                    $"Direct mapping '{dm.Id}': no {dm.TargetCkTypeId} target matched " +
                    $"(name '{dm.TargetName}', rtId '{dm.TargetRtId}').");
                continue;
            }

            var matchingControls = allControls.Where(ctrl => DirectControlMatches(dm, ctrl)).ToList();
            if (matchingControls.Count == 0)
            {
                nodeContext.Warning(
                    $"Direct mapping '{dm.Id}': no source control matched " +
                    $"(type '{dm.ControlType}', name '{dm.ControlName ?? dm.ControlNameRegex}').");
                continue;
            }

            foreach (var control in matchingControls)
            {
                var hasStatesArray = ControlHasStatesArray(control, c.StatesAttribute);
                foreach (var st in dm.States)
                {
                    if (string.IsNullOrWhiteSpace(st.StateName) || string.IsNullOrWhiteSpace(st.TargetAttribute))
                    {
                        continue;
                    }

                    // When the control publishes a States array, only emit for states it actually has
                    // (so a non-bidirectional meter produces no totalNeg/ExportedEnergy mapping).
                    if (hasStatesArray &&
                        !ControlHasState(control, c.StatesAttribute, c.StateNameAttribute, st.StateName!))
                    {
                        continue;
                    }

                    var controlRtId = control.RtId.ToString();
                    var name = $"{dm.Id}|{controlRtId}|{st.StateName}";
                    suggestions.Add(new MappingSuggestion(
                        name,
                        controlRtId,
                        c.SourceControlCkTypeId,
                        target.RtId.ToString(),
                        dm.TargetCkTypeId!,
                        st.StateName!,
                        st.TargetAttribute!,
                        st.Expression ?? string.Empty,
                        dm.Id!,
                        $"Direct '{dm.Id}': control '{control.GetAttributeValueOrDefault("Name") as string ?? "(unnamed)"}' " +
                        $"state '{st.StateName}' -> {dm.TargetCkTypeId}.{st.TargetAttribute}"));
                    rulesHitByRuleId[dm.Id!] = rulesHitByRuleId.GetValueOrDefault(dm.Id!) + 1;
                }
            }
        }
    }

    private static RtEntity? ResolveDirectTarget(DirectControlMappingConfiguration dm, List<RtEntity> targets)
    {
        if (!string.IsNullOrWhiteSpace(dm.TargetRtId))
        {
            return targets.FirstOrDefault(t =>
                string.Equals(t.RtId.ToString(), dm.TargetRtId, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(dm.TargetName))
        {
            return targets.FirstOrDefault(t =>
                string.Equals(t.GetAttributeValueOrDefault("Name") as string, dm.TargetName, StringComparison.Ordinal));
        }
        return null;
    }

    private static bool DirectControlMatches(DirectControlMappingConfiguration dm, RtEntity control)
    {
        if (!string.IsNullOrWhiteSpace(dm.ControlType))
        {
            var ct = control.GetAttributeValueOrDefault("ControlType") as string;
            if (!string.Equals(ct, dm.ControlType, StringComparison.Ordinal)) return false;
        }

        var name = control.GetAttributeValueOrDefault("Name") as string ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dm.ControlName))
        {
            return string.Equals(name, dm.ControlName, StringComparison.Ordinal);
        }
        if (!string.IsNullOrWhiteSpace(dm.ControlNameRegex))
        {
            return Regex.IsMatch(name, dm.ControlNameRegex);
        }
        return false; // a direct mapping must identify the control by name or regex
    }

    private static bool ControlHasStatesArray(RtEntity control, string statesAttribute)
    {
        var v = control.GetAttributeValueOrDefault(statesAttribute);
        return v switch
        {
            string => false,
            IEnumerable<RtRecord> => true,
            System.Collections.IEnumerable e => e.OfType<RtRecord>().Any(),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates a single rule against a control. Returns the number of suggestions emitted
    /// (0 if the rule does not match; 1 for a matching rule).
    /// </summary>
    private static int TryEmitRule(
        ControlMappingRuleConfiguration rule,
        RtEntity control,
        RtEntity? category,
        RtEntity target,
        string targetCkTypeId,
        string containerName,
        GenerateDataPointMappingsNodeConfiguration c,
        List<MappingSuggestion> suggestions)
    {
        var when = rule.When;

        var controlType = control.GetAttributeValueOrDefault("ControlType") as string;
        if (!string.IsNullOrWhiteSpace(when.ControlType) &&
            !string.Equals(controlType, when.ControlType, StringComparison.Ordinal))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(when.ControlNameRegex))
        {
            var controlName = control.GetAttributeValueOrDefault("Name") as string ?? string.Empty;
            if (!Regex.IsMatch(controlName, when.ControlNameRegex))
            {
                return 0;
            }
        }

        if (!string.IsNullOrWhiteSpace(when.CategoryType))
        {
            var catType = category?.GetAttributeValueOrDefault("CategoryType") as string;
            if (!string.Equals(catType, when.CategoryType, StringComparison.Ordinal))
            {
                return 0;
            }
        }

        if (!string.IsNullOrWhiteSpace(when.CategoryNameRegex))
        {
            var catName = category?.GetAttributeValueOrDefault("Name") as string ?? string.Empty;
            if (!Regex.IsMatch(catName, when.CategoryNameRegex))
            {
                return 0;
            }
        }

        // Resolve effective sourceAttributePath. When the rule names a stateName, the control must
        // expose that state — otherwise the rule does not apply (e.g. an IRoomControllerV2 variant
        // that doesn't publish co2 should not produce a co2 mapping).
        string sourceAttributePath;
        if (!string.IsNullOrWhiteSpace(when.StateName))
        {
            if (!ControlHasState(control, c.StatesAttribute, c.StateNameAttribute, when.StateName))
            {
                return 0;
            }
            sourceAttributePath = when.StateName!;
        }
        else
        {
            sourceAttributePath = c.DefaultSourceAttributePath;
        }

        var controlRtId = control.RtId.ToString();
        var name = $"{rule.Id}|{controlRtId}|{sourceAttributePath}";

        var suggestion = new MappingSuggestion(
            name,
            controlRtId,
            c.SourceControlCkTypeId,
            // Output field name "spaceRtId" / "spaceCkTypeId" is historical (matches the
            // AnthropicAiQueryNode contract); when a rule resolves a child target, these
            // fields carry the child's identity (e.g. a TemperatureSensor), not the
            // container's. Downstream pipelines see the correct mapping target either way.
            target.RtId.ToString(),
            targetCkTypeId,
            sourceAttributePath,
            rule.Map.TargetAttribute!,
            rule.Map.Expression ?? string.Empty,
            rule.Id!,
            $"Container '{containerName}' matched; rule '{rule.Id}' on control '{control.GetAttributeValueOrDefault("Name") as string ?? "(unnamed)"}'");
        suggestions.Add(suggestion);
        return 1;
    }

    /// <summary>
    /// Typed shape of a single DataPointMapping suggestion. The serialized key names/order
    /// (camelCase, fixed order) match the AnthropicAiQueryNode output contract so the same
    /// downstream ForEach consumes both. Set&lt;T&gt; reproduces the former JsonObject bytes.
    /// </summary>
    internal sealed record MappingSuggestion(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("controlRtId")] string ControlRtId,
        [property: JsonPropertyName("controlCkTypeId")] string ControlCkTypeId,
        [property: JsonPropertyName("spaceRtId")] string SpaceRtId,
        [property: JsonPropertyName("spaceCkTypeId")] string SpaceCkTypeId,
        [property: JsonPropertyName("sourceAttributePath")] string SourceAttributePath,
        [property: JsonPropertyName("targetAttributePath")] string TargetAttributePath,
        [property: JsonPropertyName("mappingExpression")] string MappingExpression,
        [property: JsonPropertyName("ruleId")] string RuleId,
        [property: JsonPropertyName("reason")] string Reason);

    /// <summary>Typed shape of the optional statistics object written to StatisticsTargetPath.</summary>
    internal sealed record MappingStatistics(
        [property: JsonPropertyName("totalContainers")] int TotalContainers,
        [property: JsonPropertyName("matchedContainers")] int MatchedContainers,
        [property: JsonPropertyName("unmatchedContainers")] int UnmatchedContainers,
        [property: JsonPropertyName("unmatchedContainerNames")] IReadOnlyList<string> UnmatchedContainerNames,
        [property: JsonPropertyName("totalSuggestions")] int TotalSuggestions,
        [property: JsonPropertyName("ruleHits")] IReadOnlyDictionary<string, int> RuleHits,
        [property: JsonPropertyName("definedRuleIds")] IReadOnlyList<string> DefinedRuleIds);

    internal static bool ControlHasState(RtEntity control, string statesAttribute, string stateNameAttribute,
        string stateName)
    {
        var statesValue = control.GetAttributeValueOrDefault(statesAttribute);
        var stateRecords = statesValue switch
        {
            IEnumerable<RtRecord> records => records,
            System.Collections.IEnumerable enumerable => enumerable.OfType<RtRecord>(),
            _ => null
        };

        if (stateRecords == null) return false;

        foreach (var record in stateRecords)
        {
            var name = record.GetAttributeValueOrDefault(stateNameAttribute) as string;
            if (string.Equals(name, stateName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Encapsulates the ordered, first-match-wins container matching algorithm.
    /// Lookup tables for the NormalizedName / Regex strategies are pre-built per target list.
    /// </summary>
    internal sealed class ContainerMatcher
    {
        private readonly ICollection<ContainerMatchingStrategyConfiguration> _strategies;
        private readonly List<RtEntity> _targets;
        // strategy index → (targetAttribute → normalized lookup)
        private readonly Dictionary<int, Dictionary<string, RtEntity>> _normalizedIndex = new();
        // RtId string → entity
        private readonly Dictionary<string, RtEntity> _byRtId;

        public ContainerMatcher(ICollection<ContainerMatchingStrategyConfiguration> strategies, List<RtEntity> targets)
        {
            _strategies = strategies;
            _targets = targets;
            _byRtId = targets.ToDictionary(t => t.RtId.ToString(), StringComparer.Ordinal);

            var i = 0;
            foreach (var s in strategies)
            {
                if (s.Kind == ContainerMatchingStrategyKind.NormalizedName ||
                    s.Kind == ContainerMatchingStrategyKind.Regex)
                {
                    var lookup = new Dictionary<string, RtEntity>(StringComparer.Ordinal);
                    foreach (var t in targets)
                    {
                        var raw = t.GetAttributeValueOrDefault(s.TargetAttribute) as string;
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var normalized = Normalize(raw);
                        if (!lookup.ContainsKey(normalized))
                        {
                            lookup.Add(normalized, t);
                        }
                    }
                    _normalizedIndex[i] = lookup;
                }
                i++;
            }
        }

        public RtEntity? Match(RtEntity container)
        {
            var i = 0;
            foreach (var s in _strategies)
            {
                var match = TryStrategy(s, i, container);
                if (match != null) return match;
                i++;
            }
            return null;
        }

        private RtEntity? TryStrategy(ContainerMatchingStrategyConfiguration s, int index, RtEntity container)
        {
            switch (s.Kind)
            {
                case ContainerMatchingStrategyKind.ExactName:
                    {
                        var srcVal = container.GetAttributeValueOrDefault(s.SourceAttribute) as string;
                        if (string.IsNullOrWhiteSpace(srcVal)) return null;
                        return _targets.FirstOrDefault(t =>
                            string.Equals(t.GetAttributeValueOrDefault(s.TargetAttribute) as string,
                                srcVal, StringComparison.Ordinal));
                    }
                case ContainerMatchingStrategyKind.NormalizedName:
                    {
                        var srcVal = container.GetAttributeValueOrDefault(s.SourceAttribute) as string;
                        if (string.IsNullOrWhiteSpace(srcVal)) return null;
                        var key = Normalize(srcVal);
                        return _normalizedIndex.TryGetValue(index, out var lookup) &&
                               lookup.TryGetValue(key, out var match)
                            ? match
                            : null;
                    }
                case ContainerMatchingStrategyKind.Regex:
                    {
                        if (string.IsNullOrWhiteSpace(s.Pattern)) return null;
                        var srcVal = container.GetAttributeValueOrDefault(s.SourceAttribute) as string;
                        if (string.IsNullOrWhiteSpace(srcVal)) return null;
                        var m = Regex.Match(srcVal, s.Pattern);
                        if (!m.Success) return null;
                        var groupIdx = Math.Max(0, s.CaptureGroup);
                        if (groupIdx >= m.Groups.Count) return null;
                        var capture = m.Groups[groupIdx].Value;
                        if (string.IsNullOrWhiteSpace(capture)) return null;
                        var key = Normalize(capture);
                        return _normalizedIndex.TryGetValue(index, out var lookup) &&
                               lookup.TryGetValue(key, out var match)
                            ? match
                            : null;
                    }
                case ContainerMatchingStrategyKind.Manual:
                    {
                        if (s.Overrides == null) return null;
                        var srcVal = container.GetAttributeValueOrDefault(s.SourceAttribute) as string;
                        if (string.IsNullOrWhiteSpace(srcVal)) return null;
                        foreach (var ov in s.Overrides)
                        {
                            if (!string.Equals(ov.Source, srcVal, StringComparison.Ordinal)) continue;
                            if (!string.IsNullOrWhiteSpace(ov.TargetRtId) &&
                                _byRtId.TryGetValue(ov.TargetRtId!, out var byId))
                            {
                                return byId;
                            }
                            if (!string.IsNullOrWhiteSpace(ov.TargetName))
                            {
                                return _targets.FirstOrDefault(t =>
                                    string.Equals(t.GetAttributeValueOrDefault(s.TargetAttribute) as string,
                                        ov.TargetName, StringComparison.Ordinal));
                            }
                        }
                        return null;
                    }
                default:
                    return null;
            }
        }

        // Lowercase, trim, strip diacritics, collapse whitespace + punctuation. Result is the
        // canonical key for fuzzy-but-deterministic name matching.
        internal static string Normalize(string s)
        {
            var normalized = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch)) continue;
                if (char.IsPunctuation(ch)) continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}

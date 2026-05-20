using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

        var suggestions = new JArray();
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

            foreach (var (control, category) in controls)
            {
                foreach (var rule in c.ControlMappingRules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Map.TargetAttribute))
                    {
                        continue;
                    }

                    var emitted = TryEmitRule(rule, control, category, target, containerName, c, suggestions);
                    if (emitted > 0)
                    {
                        rulesHitByRuleId[rule.Id!] = rulesHitByRuleId.GetValueOrDefault(rule.Id!) + emitted;
                    }
                }
            }
        }

        nodeContext.Info(
            $"Matched {matched}/{containers.Count} containers, produced {suggestions.Count} mapping suggestions " +
            $"across {rulesHitByRuleId.Count} rule(s).");
        if (unmatched.Count > 0)
        {
            nodeContext.Warning(
                $"{unmatched.Count} container(s) had no matching target: " +
                string.Join(", ", unmatched.Take(20)) + (unmatched.Count > 20 ? ", …" : ""));
        }

        dataContext.SetValueByPath(c.TargetPath, suggestions, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        if (!string.IsNullOrWhiteSpace(c.StatisticsTargetPath))
        {
            var statistics = new JObject
            {
                ["totalContainers"] = containers.Count,
                ["matchedContainers"] = matched,
                ["unmatchedContainers"] = unmatched.Count,
                ["unmatchedContainerNames"] = new JArray(unmatched),
                ["totalSuggestions"] = suggestions.Count,
                ["ruleHits"] = JObject.FromObject(rulesHitByRuleId),
                ["definedRuleIds"] = new JArray(rulesById.Keys)
            };
            dataContext.SetValueByPath(c.StatisticsTargetPath, statistics, c.DocumentMode, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite, RtNewtonsoftSerializer.DefaultSerializer);
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
    /// Evaluates a single rule against a control. Returns the number of suggestions emitted
    /// (0 if the rule does not match; 1 for a matching rule).
    /// </summary>
    private static int TryEmitRule(
        ControlMappingRuleConfiguration rule,
        RtEntity control,
        RtEntity? category,
        RtEntity target,
        string containerName,
        GenerateDataPointMappingsNodeConfiguration c,
        JArray suggestions)
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

        var suggestion = new JObject
        {
            ["name"] = name,
            ["controlRtId"] = controlRtId,
            ["controlCkTypeId"] = c.SourceControlCkTypeId,
            ["spaceRtId"] = target.RtId.ToString(),
            ["spaceCkTypeId"] = c.TargetCkTypeId,
            ["sourceAttributePath"] = sourceAttributePath,
            ["targetAttributePath"] = rule.Map.TargetAttribute,
            ["mappingExpression"] = rule.Map.Expression ?? string.Empty,
            ["ruleId"] = rule.Id,
            ["reason"] = $"Container '{containerName}' matched; rule '{rule.Id}' on control '{control.GetAttributeValueOrDefault("Name") as string ?? "(unnamed)"}'"
        };
        suggestions.Add(suggestion);
        return 1;
    }

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

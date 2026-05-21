using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the GenerateDataPointMappings node.
///
/// The node deterministically generates DataPointMapping suggestion records by:
/// 1. Loading source containers (e.g. Loxone/Room) and target entities (e.g. EnergyIQ/Space).
/// 2. Matching containers to targets using an ordered list of strategies — first match wins.
/// 3. Traversing each matched container's hierarchy to reach control entities
///    (e.g. Room → Category → Control via System/ParentChild).
/// 4. Evaluating <see cref="ControlMappingRules"/> against each control + its state set,
///    producing one suggestion entry per matched (rule, state) pair.
///
/// The output shape is identical to the AnthropicAiQuery suggestion array, so the
/// downstream pipeline (GetOrCreate + CreateUpdateInfo + CreateAssociationUpdate) can
/// consume both sources interchangeably.
/// </summary>
[NodeName("GenerateDataPointMappings", 1)]
public record GenerateDataPointMappingsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// CkTypeId of the container entities on the source side (e.g. "Loxone/Room").
    /// One container ≡ one target candidate.
    /// </summary>
    [PropertyGroup("Source", 0)]
    public required string SourceContainerCkTypeId { get; set; }

    /// <summary>
    /// CkTypeId of the leaf entities to be mapped (e.g. "Loxone/Control").
    /// These are reached by walking the hierarchy from each matched container.
    /// </summary>
    [PropertyGroup("Source", 1)]
    public required string SourceControlCkTypeId { get; set; }

    /// <summary>
    /// Optional CkTypeId of an intermediate hierarchy level (e.g. "Loxone/Category").
    /// When set, controls are reached via Container → Intermediate → Control.
    /// When null, controls are reached directly: Container → Control.
    /// Used for rules that reference categoryType / categoryName.
    /// </summary>
    [PropertyGroup("Source", 2)]
    public string? SourceCategoryCkTypeId { get; set; }

    /// <summary>
    /// AssociationRoleId used to traverse the source hierarchy (default System/ParentChild).
    /// The traversal direction is INBOUND from the container's perspective — i.e. children
    /// are entities pointing at the container with this role.
    /// </summary>
    [PropertyGroup("Source", 3)]
    public string HierarchyAssociationRoleId { get; set; } = "System/ParentChild";

    /// <summary>
    /// CkTypeId of the target entities (e.g. "EnergyIQ/Space").
    /// </summary>
    [PropertyGroup("Target", 0)]
    public required string TargetCkTypeId { get; set; }

    /// <summary>
    /// Attribute names on the source control that hold a RecordArray of states.
    /// Each state record must expose <see cref="StateNameAttribute"/>.
    /// Default "States" (Loxone convention).
    /// </summary>
    [PropertyGroup("Source", 4)]
    public string StatesAttribute { get; set; } = "States";

    /// <summary>
    /// Attribute name within a state record that holds the state name
    /// (e.g. "Name" → "tempActual"). Default "Name".
    /// </summary>
    [PropertyGroup("Source", 5)]
    public string StateNameAttribute { get; set; } = "Name";

    /// <summary>
    /// Default sourceAttributePath emitted for rules that do not declare a stateName
    /// (single-state controls, e.g. Switch / InfoOnlyAnalog). Default "CurrentValue".
    /// </summary>
    [PropertyGroup("Options", 0)]
    public string DefaultSourceAttributePath { get; set; } = "CurrentValue";

    /// <summary>
    /// Optional path under which the node emits a JSON summary of the matching result
    /// (matched / unmatched containers, rule counts). For diagnostics only.
    /// </summary>
    [PropertyGroup("Output", 0, "jsonpath")]
    public string? StatisticsTargetPath { get; set; }

    /// <summary>
    /// Container ↔ target matching strategies, evaluated in order. First match wins.
    /// </summary>
    [PropertyGroup("Container Matching", 0)]
    public ICollection<ContainerMatchingStrategyConfiguration> ContainerMatchingStrategies { get; set; } =
        new List<ContainerMatchingStrategyConfiguration>();

    /// <summary>
    /// Rules describing which control + state combinations produce which target attribute.
    /// Every rule whose <see cref="ControlMappingRuleConfiguration.When"/> matches a control
    /// produces one suggestion (or one per applicable state, if a stateName is set).
    /// </summary>
    [PropertyGroup("Control Rules", 0)]
    public ICollection<ControlMappingRuleConfiguration> ControlMappingRules { get; set; } =
        new List<ControlMappingRuleConfiguration>();
}

/// <summary>
/// One strategy step for matching source containers to target entities.
/// Strategies are tried in order; the first one that resolves a target is used.
/// </summary>
public class ContainerMatchingStrategyConfiguration
{
    /// <summary>
    /// Strategy discriminator: ExactName, NormalizedName, Regex, Manual.
    /// </summary>
    public ContainerMatchingStrategyKind Kind { get; set; }

    /// <summary>
    /// Attribute on the source container whose value drives the match
    /// (default "Name").
    /// </summary>
    public string SourceAttribute { get; set; } = "Name";

    /// <summary>
    /// Attribute on the target entity to compare against (default "Name").
    /// </summary>
    public string TargetAttribute { get; set; } = "Name";

    /// <summary>
    /// For <see cref="ContainerMatchingStrategyKind.Regex"/>: pattern applied to the
    /// source attribute. The first capture group (or group 0 if no group) is the
    /// comparison key, matched against the target attribute (normalized).
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    /// For <see cref="ContainerMatchingStrategyKind.Regex"/>: 1-based capture group index
    /// (default 1). Use 0 for the entire match.
    /// </summary>
    public int CaptureGroup { get; set; } = 1;

    /// <summary>
    /// For <see cref="ContainerMatchingStrategyKind.Manual"/>: explicit container→target
    /// overrides by source attribute value (typically Name or RtId).
    /// </summary>
    public ICollection<ManualMatchOverride>? Overrides { get; set; }
}

/// <summary>
/// Container matching strategy kinds.
/// </summary>
public enum ContainerMatchingStrategyKind
{
    /// <summary>Case-sensitive exact equality on the configured attributes.</summary>
    ExactName = 0,

    /// <summary>Case-insensitive equality after lowercasing, trimming, stripping diacritics and whitespace.</summary>
    NormalizedName = 1,

    /// <summary>Regex on the source attribute, comparing the captured value against the (normalized) target attribute.</summary>
    Regex = 2,

    /// <summary>Explicit lookup via the <see cref="ContainerMatchingStrategyConfiguration.Overrides"/> list.</summary>
    Manual = 3
}

/// <summary>
/// Explicit container→target match override for the Manual strategy.
/// </summary>
public class ManualMatchOverride
{
    /// <summary>
    /// The value of the source attribute (typically Name) identifying the container.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// The RtId of the target entity this container maps to. Takes precedence over <see cref="TargetName"/>.
    /// </summary>
    public string? TargetRtId { get; set; }

    /// <summary>
    /// The value of the target attribute identifying the target (used when TargetRtId is not set).
    /// </summary>
    public string? TargetName { get; set; }
}

/// <summary>
/// One rule mapping a control + optional state to a target attribute on the matched target entity.
/// </summary>
public class ControlMappingRuleConfiguration
{
    /// <summary>
    /// Stable identifier for the rule. Used in the generated DataPointMapping Name (for idempotency)
    /// and in the diagnostics output. Required.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Selector predicate over the control + state.
    /// </summary>
    public ControlMappingRuleWhen When { get; set; } = new();

    /// <summary>
    /// What this rule produces when <see cref="When"/> matches.
    /// </summary>
    public ControlMappingRuleMap Map { get; set; } = new();
}

/// <summary>
/// Predicate over a control + state. All non-null fields must match (AND semantics).
/// </summary>
public class ControlMappingRuleWhen
{
    /// <summary>
    /// Exact control type (e.g. "IRoomControllerV2", "Dimmer", "Jalousie").
    /// </summary>
    public string? ControlType { get; set; }

    /// <summary>
    /// Optional state name to match (e.g. "tempActual"). When set, the rule fires only for
    /// controls whose <see cref="GenerateDataPointMappingsNodeConfiguration.StatesAttribute"/>
    /// contains a state record with this name. When null, the rule fires once per matched
    /// control and the suggestion uses <see cref="GenerateDataPointMappingsNodeConfiguration.DefaultSourceAttributePath"/>.
    /// </summary>
    public string? StateName { get; set; }

    /// <summary>
    /// Optional category type filter (compared against the category's CategoryType attribute).
    /// Requires SourceCategoryCkTypeId to be set on the node configuration.
    /// </summary>
    public string? CategoryType { get; set; }

    /// <summary>
    /// Optional case-insensitive regex applied to the category's Name attribute.
    /// </summary>
    public string? CategoryNameRegex { get; set; }

    /// <summary>
    /// Optional case-insensitive regex applied to the control's Name attribute
    /// (e.g. "(?i)(feuchte|humid)" to detect humidity sensors by naming).
    /// </summary>
    public string? ControlNameRegex { get; set; }
}

/// <summary>
/// What a rule emits when it matches.
/// </summary>
public class ControlMappingRuleMap
{
    /// <summary>
    /// Target attribute path on the target entity (e.g. "Temperature", "CO2Level").
    /// </summary>
    public string? TargetAttribute { get; set; }

    /// <summary>
    /// Optional mXparser expression applied to the value at mapping evaluation time
    /// (e.g. "value / 100"). Empty string ⇒ no transformation.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Optional: navigate from the matched container target to a child entity of this
    /// CkTypeId before emitting the suggestion. Used in v2-style models where the
    /// actual mapping target is a child of the matched container (e.g. a Loxone/Room
    /// matches an EnergyIQ/Space, but the temperature reading must target an
    /// EnergyIQ/TemperatureSensor associated with that Space).
    ///
    /// When set, the node finds the first child entity of type
    /// <c>ChildTargetCkTypeId</c> reachable from the matched target via the
    /// <see cref="ChildTargetAssociationRoleId"/> role (inbound direction). If no
    /// matching child exists, the rule produces no suggestion.
    ///
    /// When null, the matched container target is used directly (v1 behaviour — back
    /// compat with rules that target attributes on the container itself).
    /// </summary>
    public string? ChildTargetCkTypeId { get; set; }

    /// <summary>
    /// Optional: association role used to resolve the child target. Defaults to the
    /// node's <see cref="GenerateDataPointMappingsNodeConfiguration.HierarchyAssociationRoleId"/>
    /// when null. Typically a v2 inverse role such as <c>EnergyIQ/SpaceSensors</c>,
    /// <c>EnergyIQ/SpaceActuators</c>, or <c>EnergyIQ/SpaceTerminals</c>.
    /// </summary>
    public string? ChildTargetAssociationRoleId { get; set; }
}

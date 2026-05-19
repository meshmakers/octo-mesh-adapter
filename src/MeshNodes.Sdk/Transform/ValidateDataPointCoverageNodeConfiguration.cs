using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// A single coverage rule: for entities of the given CK type, declare which target
/// attribute paths a DataPointMapping must (or should) cover.
/// </summary>
public record CoverageRule
{
    /// <summary>
    /// The fully qualified CK type id (e.g. "EnergyIQ/Space") this rule applies to.
    /// Polymorphic — entities of derived types also match.
    /// </summary>
    public required string CkTypeId { get; set; }

    /// <summary>
    /// Target attribute paths that MUST be present on at least one enabled mapping
    /// inbound to the entity. Missing required attributes produce status="error".
    /// </summary>
    public List<string> RequiredAttributes { get; set; } = new();

    /// <summary>
    /// Target attribute paths that SHOULD be present. Missing recommended attributes
    /// produce status="warning" (only relevant when no required attributes are missing).
    /// </summary>
    public List<string> RecommendedAttributes { get; set; } = new();
}

/// <summary>
/// Configuration for the ValidateDataPointCoverage node. Traverses a tree hierarchy
/// (root + children via the configured ParentChild role) and for every visited node
/// evaluates which target attribute paths are covered by inbound DataPointMappings.
/// Emits a JSON report at <c>TargetPath</c> that can be persisted via the
/// <c>SetPipelineExecutionResult@1</c> node so the Studio can colour-code the tree.
/// </summary>
[NodeName("ValidateDataPointCoverage", 1)]
public record ValidateDataPointCoverageNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// RtId of the tree root entity to validate. Leave empty to read from the data
    /// context at <see cref="RootRtIdPath"/>.
    /// </summary>
    [PropertyGroup("Root", 0)]
    public string? RootRtId { get; set; }

    /// <summary>
    /// CK type id of the root entity (e.g. "Basic/Tree" or a derived type). Required.
    /// </summary>
    [PropertyGroup("Root", 1)]
    public required string RootCkTypeId { get; set; }

    /// <summary>
    /// Optional JsonPath in the data context to read the root rtId from when
    /// <see cref="RootRtId"/> is empty.
    /// </summary>
    [PropertyGroup("Root", 2)]
    public string? RootRtIdPath { get; set; }

    /// <summary>
    /// Association role used to traverse parent→child relationships. The query uses
    /// INBOUND direction on the parent: children are entities whose outbound
    /// ParentChild points at the parent. Defaults to <c>System/ParentChild</c>.
    /// </summary>
    [PropertyGroup("Hierarchy", 0)]
    public string ChildRoleId { get; set; } = "System/ParentChild";

    /// <summary>
    /// CK type id of children to traverse (polymorphic; e.g. "Basic/TreeNode").
    /// Defaults to <c>Basic/TreeNode</c>.
    /// </summary>
    [PropertyGroup("Hierarchy", 1)]
    public string ChildCkTypeId { get; set; } = "Basic/TreeNode";

    /// <summary>
    /// Maximum depth to traverse from the root (1 = only direct children). Defaults
    /// to a generous value that handles normal building hierarchies.
    /// </summary>
    [PropertyGroup("Hierarchy", 2)]
    public int MaxDepth { get; set; } = 16;

    /// <summary>
    /// Association role used to find DataPointMappings pointing at a node. Defaults
    /// to <c>System.Communication/MapsTo</c>.
    /// </summary>
    [PropertyGroup("Mapping", 0)]
    public string MappingRoleId { get; set; } = "System.Communication/MapsTo";

    /// <summary>
    /// CK type id of mapping entities (polymorphic). Defaults to
    /// <c>System.Communication/DataPointMapping</c>.
    /// </summary>
    [PropertyGroup("Mapping", 1)]
    public string MappingCkTypeId { get; set; } = "System.Communication/DataPointMapping";

    /// <summary>
    /// Whether disabled DataPointMappings (Enabled=false) contribute to coverage.
    /// Defaults to <c>false</c> — disabled mappings do not count.
    /// </summary>
    [PropertyGroup("Mapping", 2)]
    public bool IncludeDisabledMappings { get; set; }

    /// <summary>
    /// Coverage rules per CK type. Entities not covered by any rule receive
    /// status="info" with empty missing/present lists.
    /// </summary>
    [PropertyGroup("Rules", 0)]
    public List<CoverageRule> Rules { get; set; } = new();
}

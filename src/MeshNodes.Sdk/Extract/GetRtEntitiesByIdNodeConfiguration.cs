using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node get rt entities by id
/// </summary>
[NodeName("GetRtEntitiesById", 1)]
public record GetRtEntitiesByIdNodeConfiguration : TargetPathNodeConfiguration
{

    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    /// <summary>
    /// Optional tenant to read from instead of the pipeline's own tenant (AB#9768): the <c>Parent</c>, a
    /// <c>Sibling</c> or a <c>Child</c> of the pipeline's tenant in the tenant registry. Without it (the default)
    /// the node reads its own tenant exactly as before. Every read goes through the cross-tenant guard: the
    /// relation is checked on the registry, the node identity must hold a right in the target that the identity
    /// service proves (<c>Caller</c> or <c>ServiceAccount</c>; <c>System</c> is always refused), and a refusal
    /// fails the execution with <c>CROSS_TENANT_FORBIDDEN</c>. See docs/cross-tenant-access.md.
    /// </summary>
    [PropertyGroup("Execution", 101)]
    public TenantTargetConfiguration? TargetTenant { get; set; }
    /// <summary>
    /// Runtime construction kit type id to filter by (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the json path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Amount of items to skip
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int? Skip { get; set; }

    /// <summary>
    /// Amount of items to take
    /// </summary>
    [PropertyGroup("Query", 1)]
    public int? Take { get; set; }

    /// <summary>
    /// Gets or sets the rt ids
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public ICollection<OctoObjectId>? RtIds { get; set; }

    /// <summary>
    /// Gets or sets the json path to the rt ids
    /// </summary>
    [PropertyGroup("Entity", 3, "jsonpath")]
    public string? RtIdsPath { get; set; }

    /// <summary>
    /// A list of field filters
    /// </summary>
    [PropertyGroup("Query", 2)]
    public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
}
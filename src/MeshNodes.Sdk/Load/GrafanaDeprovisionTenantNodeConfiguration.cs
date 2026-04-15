using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Deprovisions a Grafana organization for the current tenant.
/// Deletes the org and all its datasources/dashboards.
/// Reads connection parameters from a GrafanaConfiguration runtime entity.
/// </summary>
[NodeName("GrafanaDeprovisionTenant", 1)]
public record GrafanaDeprovisionTenantNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the global GrafanaConfiguration entity containing Grafana connection parameters
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Path to the tenant ID in the data context. If not set, the pipeline's tenant ID is used.
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string? TenantIdPath { get; set; }
}

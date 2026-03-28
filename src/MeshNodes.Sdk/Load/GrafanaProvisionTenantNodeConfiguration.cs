using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Provisions a Grafana organization and datasource for the current tenant.
/// Creates the org if it doesn't exist and adds an OctoMesh datasource configured for the tenant.
/// Reads connection parameters from a GrafanaConfiguration runtime entity.
/// </summary>
[NodeName("GrafanaProvisionTenant", 1)]
public record GrafanaProvisionTenantNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the global GrafanaConfiguration entity containing Grafana connection parameters
    /// </summary>
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Path to the tenant ID in the data context. If not set, the pipeline's tenant ID is used.
    /// </summary>
    public string? TenantIdPath { get; set; }
}

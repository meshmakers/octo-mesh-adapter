using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that deprovisions (deletes) a Grafana organization for a tenant.
/// Reads Grafana connection parameters from a <c>GrafanaConfiguration</c> runtime entity.
/// </summary>
[NodeConfiguration(typeof(GrafanaDeprovisionTenantNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GrafanaDeprovisionTenantNode(NodeDelegate next, HttpClient httpClient, IMeshEtlContext etlContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GrafanaDeprovisionTenantNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.ServerConfiguration), c.ServerConfiguration);
        }

        var config = etlContext.GlobalConfiguration.GetValue<GrafanaConfig>(c.ServerConfiguration);

        var tenantId = !string.IsNullOrEmpty(c.TenantIdPath)
            ? dataContext.Get<string>(c.TenantIdPath)
            : etlContext.TenantId;

        if (string.IsNullOrEmpty(tenantId))
        {
            nodeContext.Error("Tenant ID is not available. Set TenantIdPath or ensure the pipeline runs in a tenant context.");
            return;
        }

        var grafanaUrl = config.GrafanaUrl.TrimEnd('/');

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.GrafanaDeprovisionTenant, new
            {
                tenantId,
                grafanaUrl,
                targetPath = c.TargetPath,
                serverConfiguration = c.ServerConfiguration
            });
            await next(dataContext, nodeContext);
            return;
        }

        var authHeader = BasicAuth(config.AdminUser, config.AdminPassword);

        nodeContext.Debug("Deprovisioning Grafana org for tenant '{0}' at {1}", tenantId, grafanaUrl);

        // Find org by name
        var request = new HttpRequestMessage(HttpMethod.Get, $"{grafanaUrl}/api/orgs/name/{tenantId}");
        request.Headers.Authorization = authHeader;

        var response = await httpClient.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            nodeContext.Debug("Grafana org '{0}' does not exist, nothing to deprovision", tenantId);

            if (!string.IsNullOrEmpty(c.TargetPath))
            {
                var notFoundResult = new JsonObject { ["provisioned"] = false, ["message"] = "Org not found" };
                dataContext.Set(c.TargetPath, notFoundResult, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode);
            }

            await next(dataContext, nodeContext);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            nodeContext.Error("Failed to find Grafana org '{0}': {1} {2}", tenantId, response.StatusCode, body);
            return;
        }

        var orgJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var orgId = orgJson?["id"]?.GetValue<long>() ?? 0;

        // Delete the org
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{grafanaUrl}/api/orgs/{orgId}");
        deleteRequest.Headers.Authorization = authHeader;

        var deleteResponse = await httpClient.SendAsync(deleteRequest);
        if (!deleteResponse.IsSuccessStatusCode)
        {
            var body = await deleteResponse.Content.ReadAsStringAsync();
            nodeContext.Error("Failed to delete Grafana org {0}: {1} {2}", orgId, deleteResponse.StatusCode, body);
            return;
        }

        nodeContext.Debug("Deleted Grafana org '{0}' (ID {1})", tenantId, orgId);

        if (!string.IsNullOrEmpty(c.TargetPath))
        {
            var result = new JsonObject
            {
                ["provisioned"] = false,
                ["orgId"] = orgId,
                ["orgName"] = tenantId,
                ["deleted"] = true
            };
            dataContext.Set(c.TargetPath, result, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static AuthenticationHeaderValue BasicAuth(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}")));

    private record GrafanaConfig
    {
        public string GrafanaUrl { get; init; } = string.Empty;
        public string AdminUser { get; init; } = string.Empty;
        public string AdminPassword { get; init; } = string.Empty;
    }
}

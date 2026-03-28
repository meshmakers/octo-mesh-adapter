using System.Net.Http.Headers;
using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that provisions a Grafana organization and OctoMesh datasource for a tenant.
/// Reads Grafana connection parameters from a <c>GrafanaConfiguration</c> runtime entity.
/// </summary>
[NodeConfiguration(typeof(GrafanaProvisionTenantNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GrafanaProvisionTenantNode(NodeDelegate next, HttpClient httpClient, IMeshEtlContext etlContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GrafanaProvisionTenantNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.ServerConfiguration), c.ServerConfiguration);
        }

        var config = etlContext.GlobalConfiguration.GetValue<GrafanaConfig>(c.ServerConfiguration);

        var tenantId = !string.IsNullOrEmpty(c.TenantIdPath)
            ? dataContext.GetSimpleValueByPath<string>(c.TenantIdPath)
            : etlContext.TenantId;

        if (string.IsNullOrEmpty(tenantId))
        {
            nodeContext.Error("Tenant ID is not available. Set TenantIdPath or ensure the pipeline runs in a tenant context.");
            return;
        }

        var grafanaUrl = config.GrafanaUrl.TrimEnd('/');
        var authHeader = BasicAuth(config.AdminUser, config.AdminPassword);

        nodeContext.Debug("Provisioning Grafana org for tenant '{0}' at {1}", tenantId, grafanaUrl);

        // 1. Check if org exists
        var orgId = await GetOrgIdByName(grafanaUrl, authHeader, tenantId, nodeContext);

        if (orgId == null)
        {
            // 2. Create org
            orgId = await CreateOrg(grafanaUrl, authHeader, tenantId, nodeContext);
            if (orgId == null)
            {
                nodeContext.Error("Failed to create Grafana org for tenant '{0}'", tenantId);
                return;
            }

            nodeContext.Debug("Created Grafana org '{0}' with ID {1}", tenantId, orgId);
        }
        else
        {
            nodeContext.Debug("Grafana org '{0}' already exists with ID {1}", tenantId, orgId);
        }

        // 3. Create datasource in org
        var dsCreated = await CreateDatasourceInOrg(grafanaUrl, authHeader, orgId.Value, tenantId, config, nodeContext);

        if (!string.IsNullOrEmpty(c.TargetPath))
        {
            var result = new JObject
            {
                ["orgId"] = orgId.Value,
                ["orgName"] = tenantId,
                ["datasourceCreated"] = dsCreated,
                ["provisioned"] = true
            };
            dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, result);
        }

        await next(dataContext, nodeContext);
    }

    private async Task<long?> GetOrgIdByName(string grafanaUrl, AuthenticationHeaderValue auth,
        string orgName, INodeContext nodeContext)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{grafanaUrl}/api/orgs/name/{orgName}");
        request.Headers.Authorization = auth;

        var response = await httpClient.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            nodeContext.Warning("Failed to check Grafana org '{0}': {1} {2}", orgName, response.StatusCode, body);
            return null;
        }

        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        return json["id"]?.Value<long>();
    }

    private async Task<long?> CreateOrg(string grafanaUrl, AuthenticationHeaderValue auth,
        string orgName, INodeContext nodeContext)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{grafanaUrl}/api/orgs");
        request.Headers.Authorization = auth;
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { name = orgName }),
            Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            nodeContext.Error("Failed to create Grafana org '{0}': {1} {2}", orgName, response.StatusCode, body);
            return null;
        }

        var json = JObject.Parse(body);
        return json["orgId"]?.Value<long>();
    }

    private async Task<bool> CreateDatasourceInOrg(string grafanaUrl, AuthenticationHeaderValue auth,
        long orgId, string tenantId, GrafanaConfig config, INodeContext nodeContext)
    {
        var dsPayload = new
        {
            name = "OctoMesh",
            type = "grafana-octo-mesh-datasource",
            url = config.OctoMeshUrl,
            access = "proxy",
            isDefault = true,
            jsonData = new
            {
                tenantId,
                identityServerUrl = config.IdentityServerUrl,
                oauthClientId = config.OAuthClientId ?? "grafana-datasource",
                oauthScopes = "openid profile email octo_api offline_access"
            },
            secureJsonData = new
            {
                grafanaAdminUser = config.AdminUser,
                grafanaAdminPassword = config.AdminPassword
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{grafanaUrl}/api/datasources");
        request.Headers.Authorization = auth;
        request.Headers.Add("X-Grafana-Org-Id", orgId.ToString());
        request.Content = new StringContent(
            JsonConvert.SerializeObject(dsPayload),
            Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);

        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            nodeContext.Debug("Datasource created/exists in org {0}", orgId);
            return true;
        }

        var body = await response.Content.ReadAsStringAsync();
        nodeContext.Warning("Failed to create datasource in org {0}: {1} {2}", orgId, response.StatusCode, body);
        return false;
    }

    private static AuthenticationHeaderValue BasicAuth(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}")));

    // Internal record to deserialize the GrafanaConfiguration entity attributes
    private record GrafanaConfig
    {
        public string GrafanaUrl { get; init; } = string.Empty;
        public string AdminUser { get; init; } = string.Empty;
        public string AdminPassword { get; init; } = string.Empty;
        public string OctoMeshUrl { get; init; } = string.Empty;
        public string IdentityServerUrl { get; init; } = string.Empty;
        public string? OAuthClientId { get; init; }
    }
}

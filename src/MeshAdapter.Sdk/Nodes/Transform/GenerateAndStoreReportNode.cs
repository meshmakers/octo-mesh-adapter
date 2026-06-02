using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using System.Text;
using System.Text.Json.Nodes;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Node that tiggers to generate a report.
/// </summary>
/// <param name="next"></param>
/// <param name="meshAdapterConfiguration"></param>
/// <param name="httpClient"></param>
/// <param name="meshEtlContext"></param>
[NodeConfiguration(typeof(GenerateAndStoreReportNodeConfiguration))]
public class GenerateAndStoreReportNode(
    NodeDelegate next,
    IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
    HttpClient httpClient,
    IMeshEtlContext meshEtlContext) : IPipelineNode
{
    /// <summary>
    /// run the HTTP request
    /// </summary>
    /// <param name="dataContext"></param>
    /// <param name="nodeContext"></param>
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GenerateAndStoreReportNodeConfiguration>();

        // Validate configuration
        if (!ValidateConfiguration(c, nodeContext))
        {
            return;
        }

        try
        {
            var isRelatedCkTypeConfigured = CkTypeIdHelper.TryResolveRtCkTypeId(c.RelatedCkTypeId, c.RelatedCkTypeIdPath, dataContext,
                out var relatedCkTypeId);
            var isRtIdConfigured = RtIdHelper.TryResolveRtId(c.RelatedRtId, c.RelatedRtIdPath, dataContext, nodeContext,
                out var relatedRtId);


            var url = meshAdapterConfiguration.Value.ReportingServiceUrl.EnsureEndsWith("/") +
                      $"{meshEtlContext.TenantId}/v1/reports/generateAndStore?" +
                      $"reportUri={c.ReportDefinitionUri}&fileSystemFolderUri={c.FileSystemFolderUri}" +
                      $"&reportFileNamePrefix={c.ReportFileNamePrefix}";
            if (isRelatedCkTypeConfigured && isRtIdConfigured)
            {
                url = meshAdapterConfiguration.Value.ReportingServiceUrl.EnsureEndsWith("/") +
                          $"{meshEtlContext.TenantId}/v1/reports/generateAndStoreWithRelatedEntity?" +
                          $"reportUri={c.ReportDefinitionUri}&fileSystemFolderUri={c.FileSystemFolderUri}" +
                          $"&reportFileNamePrefix={c.ReportFileNamePrefix}&relatedEntityId={relatedCkTypeId}@{relatedRtId}";
            }

            url += AppendReportParameters(dataContext, nodeContext, c.ReportParameters);

            nodeContext.Debug("Making HTTP POST request to {0}", url);

            // Create an HTTP request message
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            // Send the request
            using var response = await httpClient.SendAsync(request);

            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                nodeContext.Debug("HTTP request successful. Status: {0}, Response: {1}",
                    response.StatusCode, responseContent);

                JsonNode? responseJson = null;

                try
                {
                    // Only treat the response as JSON when it parses to an object. See
                    // MakeHttpRequestNode for the rationale -- this preserves the
                    // legacy JObject.Parse behavior where scalars and arrays fell
                    // through to the text branch.
                    responseJson = JsonNode.Parse(responseContent) as JsonObject;
                }
                catch (Exception)
                {
                    // this is fine, the response is not JSON
                }

                // Store response in data context at the configured path
                if (responseJson != null)
                {
                    dataContext.Set(c.TargetPath, responseJson, c.DocumentMode, c.TargetValueKind,
                        c.TargetValueWriteMode);
                }
                else
                {
                    dataContext.Set(c.TargetPath, responseContent, c.DocumentMode, c.TargetValueKind,
                        c.TargetValueWriteMode);
                }
            }
            else
            {
                nodeContext.Error("HTTP request failed. Status: {0}, Response: {1}",
                    response.StatusCode, responseContent);
                return;
            }
        }
        catch (Exception ex)
        {
            nodeContext.Error(ex, "Error making HTTP request");
            return;
        }

        await next(dataContext, nodeContext);
    }

    private static bool ValidateConfiguration(GenerateAndStoreReportNodeConfiguration config, INodeContext nodeContext)
    {
        // Validate TargetPath
        if (string.IsNullOrWhiteSpace(config.TargetPath))
        {
            nodeContext.Error("TargetPath is not set. Please specify where to store the HTTP response");
            return false;
        }

        // Validate path parameters
        foreach (var pathParam in config.ReportParameters)
        {
            if (string.IsNullOrWhiteSpace(pathParam.Name))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterNameMissing(nodeContext);
            }

            if (string.IsNullOrWhiteSpace(pathParam.Value) && string.IsNullOrWhiteSpace(pathParam.ValuePath))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(nodeContext, pathParam.Name);
            }
        }

        if (string.IsNullOrWhiteSpace(config.FileSystemFolderUri))
        {
            throw MeshAdapterPipelineExecutionException.FileSystemFolderUriMissing(nodeContext);
        }

        if (string.IsNullOrWhiteSpace(config.ReportDefinitionUri))
        {
            throw MeshAdapterPipelineExecutionException.ReportDefinitionUriMissing(nodeContext);
        }

        if (string.IsNullOrWhiteSpace(config.ReportFileNamePrefix))
        {
            throw MeshAdapterPipelineExecutionException.ReportFileNamePrefixMissing(nodeContext);
        }

        return true;
    }

    private static string AppendReportParameters(IDataContext dataContext, INodeContext nodeContext,
        List<HttpPathParameter> pathParameters)
    {
        StringBuilder sb = new StringBuilder();
        foreach (var pathParam in pathParameters)
        {
            var value = GetParameterValue(dataContext, pathParam);
            if (value != null)
            {
                sb.Append($"&{pathParam.Name}={value}");
                nodeContext.Debug("Added reporting parameter {0} with value {1}", pathParam.Name, value);
            }
            else
            {
                nodeContext.Warning("Added reporting {0} value is null or empty", pathParam.Name);
            }
        }

        return sb.ToString();
    }

    private static string? GetParameterValue(IDataContext dataContext, HttpPathParameter pathParam)
    {
        if (!string.IsNullOrWhiteSpace(pathParam.Value))
        {
            return pathParam.Value;
        }

        if (!string.IsNullOrWhiteSpace(pathParam.ValuePath))
        {
            var value = dataContext.Get<string>(pathParam.ValuePath);
            return value;
        }

        return null;
    }
}
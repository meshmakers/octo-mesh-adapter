using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Node that makes an HTTP request.
/// </summary>
/// <param name="next"></param>
/// <param name="httpClient"></param>
[NodeConfiguration(typeof(MakeHttpRequestNodeConfiguration))]
public class MakeHttpRequestNode(NodeDelegate next, HttpClient httpClient) : IPipelineNode
{
    /// <summary>
    /// run the HTTP request
    /// </summary>
    /// <param name="dataContext"></param>
    /// <param name="nodeContext"></param>
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<MakeHttpRequestNodeConfiguration>();

        // Validate configuration
        if (!ValidateConfiguration(c, nodeContext))
        {
            return;
        }

        try
        {
            // Get the URL
            var url = GetUrl(dataContext, c);
            if (string.IsNullOrWhiteSpace(url))
            {
                nodeContext.Error("URL is not set. Please provide a Url or UrlPath");
                return;
            }

            // Replace path parameters in URL
            url = ReplacePathParameters(dataContext, nodeContext, url, c.PathParameters);

            nodeContext.Debug("Making HTTP {0} request to {1}", c.Method, url);

            // Create HTTP request message
            using var request = new HttpRequestMessage(new(c.Method), url);

            // Add headers
            AddHeaders(dataContext, nodeContext, request, c.HeaderParameters);

            // Add body for non-GET requests
            if (!string.Equals(c.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                var body = GetBody(dataContext, c);
                if (!string.IsNullOrEmpty(body))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
            }

            // Send the request
            using var response = await httpClient.SendAsync(request);

            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                nodeContext.Debug("HTTP request successful. Status: {0}, Response: {1}",
                    response.StatusCode, responseContent);

                JToken? responseJson = null;

                try
                {
                    responseJson = JObject.Parse(responseContent);
                }
                catch (Exception)
                {
                    // this is fine, the response is not json
                }

                // Store response in data context at the configured path
                dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode, responseJson ?? responseContent);
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

    private static bool ValidateConfiguration(MakeHttpRequestNodeConfiguration config, INodeContext nodeContext)
    {
        // Validate URL configuration
        if (string.IsNullOrWhiteSpace(config.Url) && string.IsNullOrWhiteSpace(config.UrlPath))
        {
            nodeContext.Error("URL configuration is missing. Please provide either Url or UrlPath");
            return false;
        }

        // Validate HTTP method
        if (string.IsNullOrWhiteSpace(config.Method))
        {
            nodeContext.Error("HTTP Method is not set");
            return false;
        }

        var validMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };
        if (!validMethods.Contains(config.Method.ToUpperInvariant()))
        {
            nodeContext.Error("Invalid HTTP method '{0}'. Valid methods are: {1}",
                config.Method, string.Join(", ", validMethods));
            return false;
        }

        // Validate TargetPath
        if (string.IsNullOrWhiteSpace(config.TargetPath))
        {
            nodeContext.Error("TargetPath is not set. Please specify where to store the HTTP response");
            return false;
        }

        // Validate path parameters
        foreach (var pathParam in config.PathParameters)
        {
            if (string.IsNullOrWhiteSpace(pathParam.Name))
            {
                nodeContext.Error("Path parameter name is missing");
                return false;
            }

            if (string.IsNullOrWhiteSpace(pathParam.Value) && string.IsNullOrWhiteSpace(pathParam.ValuePath))
            {
                nodeContext.Error("Path parameter '{0}' must have either Value or ValuePath set", pathParam.Name);
                return false;
            }
        }

        // Validate header parameters
        foreach (var headerParam in config.HeaderParameters)
        {
            if (string.IsNullOrWhiteSpace(headerParam.Name))
            {
                nodeContext.Error("Header parameter name is missing");
                return false;
            }

            if (string.IsNullOrWhiteSpace(headerParam.Value) && string.IsNullOrWhiteSpace(headerParam.ValuePath))
            {
                nodeContext.Error("Header parameter '{0}' must have either Value or ValuePath set", headerParam.Name);
                return false;
            }
        }

        return true;
    }

    private static string GetUrl(IDataContext dataContext, MakeHttpRequestNodeConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            return config.Url;
        }

        if (!string.IsNullOrWhiteSpace(config.UrlPath))
        {
            return dataContext.GetSimpleValueByPath<string>(config.UrlPath) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? GetBody(IDataContext dataContext, MakeHttpRequestNodeConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Body))
        {
            return config.Body;
        }

        if (!string.IsNullOrWhiteSpace(config.BodyPath))
        {
            var d = dataContext.GetComplexObjectByPath<JToken>(config.BodyPath);
            return d!.ToString();
        }

        return null;
    }

    private static string ReplacePathParameters(IDataContext dataContext, INodeContext nodeContext, string url,
        List<HttpPathParameter> pathParameters)
    {
        foreach (var pathParam in pathParameters)
        {
            var value = GetParameterValue(dataContext, pathParam);
            if (value != null)
            {
                var placeholder = "{" + pathParam.Name + "}";
                url = url.Replace(placeholder, value, StringComparison.OrdinalIgnoreCase);
                nodeContext.Debug("Replaced path parameter {0} with value {1}", pathParam.Name, value);
            }
            else
            {
                nodeContext.Warning("Path parameter {0} value is null or empty", pathParam.Name);
            }
        }

        return url;
    }

    private static void AddHeaders(IDataContext dataContext, INodeContext nodeContext, HttpRequestMessage request,
        List<HttpHeaderParameter> headerParameters)
    {
        foreach (var headerParam in headerParameters)
        {
            var value = GetParameterValue(dataContext, headerParam);
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    request.Headers.Add(headerParam.Name, value);
                    nodeContext.Debug("Added header {0} with value {1}", headerParam.Name, value);
                }
                catch (Exception ex)
                {
                    nodeContext.Warning("Failed to add header {0}: {1}", headerParam.Name, ex.Message);
                }
            }
        }
    }

    private static string? GetParameterValue(IDataContext dataContext, HttpPathParameter pathParam)
    {
        if (!string.IsNullOrWhiteSpace(pathParam.Value))
        {
            return pathParam.Value;
        }

        if (!string.IsNullOrWhiteSpace(pathParam.ValuePath))
        {
            var value = dataContext.GetSimpleValueByPath<string>(pathParam.ValuePath);
            return value;
        }

        return null;
    }

    private static string? GetParameterValue(IDataContext dataContext, HttpHeaderParameter headerParam)
    {
        if (!string.IsNullOrWhiteSpace(headerParam.Value))
        {
            return headerParam.Value;
        }

        if (!string.IsNullOrWhiteSpace(headerParam.ValuePath))
        {
            return dataContext.GetSimpleValueByPath<string>(headerParam.ValuePath);
        }

        return null;
    }
}
using System.Text;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal class HttpRequestService(IOptions<AdapterOptions> adapterOptions) : IHttpRequestService
{
    private readonly Dictionary<Tuple<string, string>, HttpRequestOptions> _routes = new();

    public HttpRouteHandle CreateRoute(HttpRequestOptions options)
    {
        var key = new Tuple<string, string>(options.Method.ToString().ToUpper(), GetUri(options.Route));
        if (!_routes.TryAdd(key, options))
        {
            throw HttpRequestException.RouteAlreadyExists(options.Route);
        }

        return new HttpRouteHandle(this, options);
    }

    public void RemoveRoute(HttpMethod method, string uri)
    {
        var key = new Tuple<string, string>(method.ToString().ToUpper(), GetUri(uri));
        _routes.Remove(key);
    }
    
    public async Task<bool> SendRequestAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var key = new Tuple<string, string>(context.Request.Method.ToUpper(), path.ToLower());
        if (!_routes.TryGetValue(key, out var route))
        {
            if (_routes.Any(x=> x.Key.Item2 == path.ToLower()))
            {
                return true;
            }
            
            return false;
        }

        JObject input = new()
        {
            ["path"] = path.ToLower(),
            ["method"] = route.Method.ToString().ToUpper()
        };
        if (context.Request.ContentLength > 0)
        {
            if (context.Request.ContentType == MimeTypes.MimeTypeJson)
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await JToken.ReadFromAsync(new JsonTextReader(reader));
                
                input["body"] = body;
            }
            else if (context.Request.ContentType == MimeTypes.MimeText)
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                
                input["body"] = body;
            }
            else if (context.Request.ContentType?.StartsWith("multipart/form-data") == true)
            {
                // Handle multipart/form-data (file uploads)
                var files = new JArray();
                var formData = new JObject();
                
                foreach (var file in context.Request.Form.Files)
                {
                    using var memoryStream = new MemoryStream();
                    await file.CopyToAsync(memoryStream);
                    var fileBytes = memoryStream.ToArray();
                    
                    files.Add(new JObject
                    {
                        ["fileName"] = file.FileName,
                        ["contentType"] = file.ContentType,
                        ["length"] = file.Length,
                        ["data"] = Convert.ToBase64String(fileBytes),
                        ["encoding"] = "base64"
                    });
                }
                
                foreach (var (formKey, formValue) in context.Request.Form)
                {
                    if (formValue.Count == 1)
                    {
                        formData[formKey] = formValue[0];
                    }
                    else
                    {
                        formData[formKey] = JToken.FromObject(formValue.ToArray());
                    }
                }
                
                if (files.Count > 0)
                {
                    input["files"] = files;
                }
                if (formData.Count > 0)
                {
                    input["formData"] = formData;
                }
                input["contentType"] = context.Request.ContentType;
            }
            else if (context.Request.ContentLength > 0)
            {
                // Handle binary data and other content types
                using var memoryStream = new MemoryStream();
                await context.Request.Body.CopyToAsync(memoryStream);
                var bytes = memoryStream.ToArray();
                
                // Check if this might be text-based content
                var contentType = context.Request.ContentType ?? string.Empty;
                if (IsTextBasedContentType(contentType))
                {
                    // Try to decode as UTF-8 text
                    try
                    {
                        var textBody = Encoding.UTF8.GetString(bytes);
                        input["body"] = textBody;
                    }
                    catch
                    {
                        // If UTF-8 decoding fails, treat as binary
                        input["body"] = Convert.ToBase64String(bytes);
                        input["bodyEncoding"] = "base64";
                    }
                }
                else
                {
                    // Binary content - encode as base64
                    input["body"] = Convert.ToBase64String(bytes);
                    input["bodyEncoding"] = "base64";
                }
                
                input["contentType"] = context.Request.ContentType;
                input["contentLength"] = context.Request.ContentLength;
            }
        }
        
        if (context.Request.Query.Count > 0)
        {
            var query = new JObject();
            foreach (var (queryKey, value) in context.Request.Query)
            {
                if (value.Count == 1 )
                {
                    var o = value[0];
                    query[queryKey] = string.IsNullOrWhiteSpace(o) ? null : JToken.FromObject(o);
                    continue;
                }
        
                query[queryKey] = JToken.FromObject(value);
            }
            input["query"] = query;
        }
        
        var r = await route.ExecuteFunc(input);
        if (r != null)
        {
            context.Response.ContentType = MimeTypes.MimeTypeJson;
            await context.Response.WriteAsync(r.ToString());
        }
        return true;
    }
    
    private string GetUri(string uri)
    {
        return $"/{adapterOptions.Value.TenantId?.ToLower()}{uri.ToLower()}";
    }
    
    private static bool IsTextBasedContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;
            
        var lowerContentType = contentType.ToLowerInvariant();
        
        // Common text-based content types
        return lowerContentType.Contains("text/") ||
               lowerContentType.Contains("application/json") ||
               lowerContentType.Contains("application/xml") ||
               lowerContentType.Contains("application/javascript") ||
               lowerContentType.Contains("application/x-www-form-urlencoded") ||
               lowerContentType.Contains("+xml") ||
               lowerContentType.Contains("+json");
    }
}
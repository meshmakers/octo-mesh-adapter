using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdentityModel;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

internal class HttpRequestService(
    IOptions<AdapterOptions> adapterOptions,
    IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
    IAdapterEventService eventService,
    ILogger<HttpRequestService> logger) : IHttpRequestService
{
    private readonly Dictionary<Tuple<string, string>, HttpRequestOptions> _routes = new();

    /// <summary>
    /// Headers carrying the caller's credentials. On a route the adapter itself authorized they are
    /// withheld from the pipeline data: the caller is already verified, the data root is echoed back
    /// in the response and can be persisted by nodes such as SetPipelineExecutionResult, and any
    /// pipeline node could forward it. An anonymous route still receives them because its trigger
    /// node has to validate the caller on its own (see FromTeamsBot).
    /// </summary>
    private static readonly HashSet<string> CredentialHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Authorization", "Proxy-Authorization", "Cookie" };

    /// <summary>
    /// Claim naming the tenant a user token was issued for. Unprefixed because the JWT options
    /// keep inbound claim types as issued.
    /// </summary>
    private const string TenantIdClaim = "tenant_id";

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

        if (!await IsCallerAuthorizedAsync(context, route))
        {
            return true;
        }

        JsonObject input = new()
        {
            ["path"] = path.ToLower(),
            ["method"] = route.Method.ToString().ToUpper()
        };

        // Expose request headers so trigger nodes can authenticate the caller
        // (e.g. FromTeamsBot validates the Bot Framework JWT in the Authorization
        // header). Existing nodes simply ignore the extra field.
        if (context.Request.Headers.Count > 0)
        {
            var headers = new JsonObject();
            foreach (var (headerKey, headerValue) in context.Request.Headers)
            {
                if (!route.AllowAnonymous && CredentialHeaders.Contains(headerKey))
                {
                    continue;
                }

                headers[headerKey] = headerValue.ToString();
            }
            input["headers"] = headers;
        }
        if (context.Request.ContentLength > 0)
        {
            if (context.Request.ContentType == MimeTypes.MimeTypeJson)
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var bodyText = await reader.ReadToEndAsync();
                input["body"] = JsonNode.Parse(bodyText);
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
                var files = new JsonArray();
                var formData = new JsonObject();

                foreach (var file in context.Request.Form.Files)
                {
                    using var memoryStream = new MemoryStream();
                    await file.CopyToAsync(memoryStream);
                    var fileBytes = memoryStream.ToArray();

                    files.Add(new JsonObject
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
                        var arr = new JsonArray();
                        foreach (var v in formValue)
                        {
                            arr.Add(v);
                        }
                        formData[formKey] = arr;
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
            var query = new JsonObject();
            foreach (var (queryKey, value) in context.Request.Query)
            {
                if (value.Count == 1)
                {
                    var o = value[0];
                    query[queryKey] = string.IsNullOrWhiteSpace(o) ? null : JsonValue.Create(o);
                    continue;
                }

                var arr = new JsonArray();
                foreach (var v in value)
                {
                    arr.Add(v);
                }
                query[queryKey] = arr;
            }
            input["query"] = query;
        }

        var r = await route.ExecuteFunc(input);
        if (r != null)
        {
            context.Response.ContentType = MimeTypes.MimeTypeJson;
            await context.Response.WriteAsync(r.ToJsonString());
        }
        return true;
    }
    
    /// <summary>
    /// Authorizes the caller of a route and records the decision in the tenant's event log.
    /// An anonymous invocation carries no caller identity and serves public webhooks, so it is
    /// always traced to the adapter log at debug level but only stored as an event when
    /// <see cref="MeshAdapterConfiguration.AuditAnonymousInvocations"/> is set.
    /// </summary>
    private async Task<bool> IsCallerAuthorizedAsync(HttpContext context, HttpRequestOptions route)
    {
        var tenantOfAdapter = adapterOptions.Value.TenantId;

        if (route.AllowAnonymous)
        {
            logger.LogDebug("Allowed anonymous {Method} {Route}", route.Method, route.Route);
            if (meshAdapterConfiguration.Value.AuditAnonymousInvocations)
            {
                await eventService.StoreDebugEventAsync(tenantOfAdapter,
                    $"Allowed anonymous {route.Method.ToString().ToUpper()} {route.Route}.");
            }

            return true;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            logger.LogWarning("Denied {Method} {Route}: no valid access token", route.Method, route.Route);
            await eventService.StoreWarningEventAsync(tenantOfAdapter,
                $"Denied {route.Method.ToString().ToUpper()} {route.Route}: no valid access token.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        var subject = context.User.FindFirstValue(JwtClaimTypes.Subject);
        var tenantId = context.User.FindFirstValue(TenantIdClaim);

        // An adapter serves exactly one tenant, so a token minted for another tenant of the same
        // installation must not reach its routes. Only user tokens are compared: a client
        // credentials token carries neither a subject nor a tenant, and allowed_tenants is
        // deliberately ignored - it drives tenant selection, not authorization. Same rule as
        // TenantAuthorizationMiddleware in octo-common-services.
        if (subject != null &&
            !string.Equals(tenantId, tenantOfAdapter, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Denied {Method} {Route} for subject {Subject}: token tenant {TokenTenant} does not serve tenant {Tenant}",
                route.Method, route.Route, subject, tenantId, tenantOfAdapter);
            await eventService.StoreWarningEventAsync(tenantOfAdapter,
                $"Denied {route.Method.ToString().ToUpper()} {route.Route} for subject {subject}: " +
                $"token of tenant '{tenantId}' does not serve this tenant.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        // A blank entry cannot match a role and would make IsInRole throw, so it is
        // skipped rather than answered with a 500 - a malformed list denies access.
        if (route.RequiredRoles.Length > 0 &&
            !route.RequiredRoles.Any(role => !string.IsNullOrWhiteSpace(role) && context.User.IsInRole(role)))
        {
            logger.LogWarning("Denied {Method} {Route} for subject {Subject}: none of the roles {RequiredRoles}",
                route.Method, route.Route, subject, route.RequiredRoles);
            await eventService.StoreWarningEventAsync(tenantOfAdapter,
                $"Denied {route.Method.ToString().ToUpper()} {route.Route} for subject {subject}: " +
                $"none of the required roles {string.Join(", ", route.RequiredRoles)}.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        var roles = context.User.FindAll(JwtClaimTypes.Role).Select(c => c.Value).ToArray();
        logger.LogInformation("Allowed {Method} {Route} for subject {Subject} of tenant {Tenant} with roles {Roles}",
            route.Method, route.Route, subject, tenantId, roles);
        await eventService.StoreInformationEventAsync(tenantOfAdapter,
            $"Allowed {route.Method.ToString().ToUpper()} {route.Route} for subject {subject} " +
            $"of tenant '{tenantId}' with roles {string.Join(", ", roles)}.");
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
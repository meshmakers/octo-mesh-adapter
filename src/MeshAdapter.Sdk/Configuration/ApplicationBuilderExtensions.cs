using Meshmakers.Octo.Sdk.MeshAdapter.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

/// <summary>
/// Extensions for the application builder
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds OctoMeshAdapter to the application builder
    /// </summary>
    /// <param name="app">Application builder</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOctoMeshAdapter(this IApplicationBuilder app)
    {
        app.UseCors();

        if (IsJwtAuthenticationUsable(app.ApplicationServices))
        {
            app.UseAuthentication();
        }

        app.UseMiddleware<DynamicRouteMiddleware>();

        return app;
    }

    /// <remarks>
    ///     The authentication middleware materializes the JWT bearer handler on the first request, and
    ///     an unusable authority makes that throw - which would turn every request into an HTTP 500,
    ///     including the observability health endpoints, so the pod never becomes ready. Skipping the
    ///     middleware degrades safely: without an authenticated principal a secured route is denied by
    ///     the route gate in <c>HttpRequestService</c>, so it fails closed rather than open, while
    ///     anonymous routes and the health endpoints keep serving.
    /// </remarks>
    private static bool IsJwtAuthenticationUsable(IServiceProvider services)
    {
        var authorityUrl = services.GetRequiredService<IOptions<MeshAdapterConfiguration>>().Value.AuthorityUrl;
        var environment = services.GetRequiredService<IHostEnvironment>();

        if (ConfigureJwtBearerOptions.IsAuthorityUsable(authorityUrl, environment))
        {
            return true;
        }

        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ApplicationBuilderExtensions))
            .LogError(
                "Adapter:AuthorityUrl (OCTO_ADAPTER__AUTHORITYURL) is '{AuthorityUrl}', which does not address an identity service this adapter can use: outside development it has to be an absolute https URL and must not point at loopback, which is where the compiled-in default resolves when nobody supplied one. JWT bearer authentication is therefore disabled and every caller of a secured FromHttpRequest@2 route is rejected with HTTP 401 until the authority is configured",
                authorityUrl);

        return false;
    }
}

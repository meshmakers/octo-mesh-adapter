using Meshmakers.Octo.Sdk.MeshAdapter.Middlewares;
using Microsoft.AspNetCore.Builder;

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
        app.UseMiddleware<DynamicRouteMiddleware>();

        return app;
    }
}

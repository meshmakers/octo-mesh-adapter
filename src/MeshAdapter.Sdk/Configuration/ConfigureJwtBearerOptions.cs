using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureJwtBearerOptions(
    IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
    IHostEnvironment environment) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    /// <summary>
    ///     Tells whether <paramref name="authorityUrl" /> can back the JWT bearer handler. An unusable
    ///     value must never reach <see cref="JwtBearerOptions.Authority" />: the handler's post-configure
    ///     step derives the metadata address from it and throws when that address is not HTTPS, and it
    ///     runs inside the authentication middleware - so a single misconfigured value answers every
    ///     request with HTTP 500, health probes included. An empty value produces exactly that, because
    ///     <c>EnsureEndsWith</c> below turns it into "/".
    /// </summary>
    internal static bool IsAuthorityUsable(string? authorityUrl, IHostEnvironment environment)
    {
        if (!Uri.TryCreate(authorityUrl, UriKind.Absolute, out var authority))
        {
            return false;
        }

        if (environment.IsDevelopment())
        {
            return authority.Scheme == Uri.UriSchemeHttps || authority.Scheme == Uri.UriSchemeHttp;
        }

        // A loopback authority is the compiled-in default, which outside development means nobody
        // supplied one. Accepting it would leave authentication registered and this guard silent,
        // and the first request carrying a token would fail fetching discovery from the pod itself
        // - a 500 where a denial belongs.
        return authority.Scheme == Uri.UriSchemeHttps && !authority.IsLoopback;
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (!IsAuthorityUsable(meshAdapterConfiguration.Value.AuthorityUrl, environment))
        {
            return;
        }

        var authorityUrl = meshAdapterConfiguration.Value.AuthorityUrl.EnsureEndsWith("/");
        options.Authority = authorityUrl;
        options.Audience = CommonConstants.OctoApi;

        // Explicitly set the valid issuer so the ISSUER check does not depend on the OIDC
        // discovery document, which is what produced IDX10204 while the identity service was
        // restarting. Signing keys are still resolved from the discovery metadata, so this
        // does not make validation work without it - it removes one reason for it to fail.
        options.TokenValidationParameters.ValidIssuer = authorityUrl;
        options.TokenValidationParameters.NameClaimType = JwtClaimTypes.Name;
        options.TokenValidationParameters.RoleClaimType = JwtClaimTypes.Role;

        // Disable inbound claim mapping so JWT claim types (sub, role, tenant_id) are preserved
        // as-is instead of being remapped to long XML namespaces. Secured trigger nodes read
        // IdentityModel JwtClaimTypes from the token.
        options.MapInboundClaims = false;

        if (environment.IsDevelopment())
        {
            options.RequireHttpsMetadata = false;
        }
    }
}

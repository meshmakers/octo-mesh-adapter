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

    public void Configure(string? name, JwtBearerOptions options)
    {
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

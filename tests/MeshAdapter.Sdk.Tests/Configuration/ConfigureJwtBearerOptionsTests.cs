using System.Security.Claims;
using FakeItEasy;
using IdentityModel;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.Tests.Configuration;

public class ConfigureJwtBearerOptionsTests
{
    private const string AuthorityUrl = "https://identity.example.com";

    private static JwtBearerOptions Configure(string? environmentName = null)
    {
        environmentName ??= Environments.Production;

        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName);

        var configuration = Options.Create(new MeshAdapterConfiguration { AuthorityUrl = AuthorityUrl });
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(configuration, environment).Configure(options);

        return options;
    }

    /// <remarks>
    /// Auditing every anonymous invocation writes a row per request into a store nothing prunes,
    /// so it must be opted into per environment. Asserted here rather than assumed because a
    /// build-conditional default would have been untestable - and would have missed both dev
    /// setups anyway, since octo-tools and the container images alike run Release builds.
    /// </remarks>
    [Fact]
    public void Configuration_DoesNotAuditAnonymousInvocationsByDefault()
    {
        Assert.False(new MeshAdapterConfiguration().AuditAnonymousInvocations);
    }

    [Fact]
    public void Configure_ValidatesAgainstTheConfiguredAuthority()
    {
        var options = Configure();

        Assert.Equal($"{AuthorityUrl}/", options.Authority);
        Assert.Equal($"{AuthorityUrl}/", options.TokenValidationParameters.ValidIssuer);
        Assert.Equal(CommonConstants.OctoApi, options.Audience);
    }

    [Fact]
    public void Configure_KeepsJwtClaimTypesUnmapped()
    {
        var options = Configure();

        Assert.False(options.MapInboundClaims);
    }

    /// <remarks>
    /// Expiry is enforced by the JWT handler rather than by our own code, so the flag that
    /// enables it is asserted here - it is a single setting an unrelated edit could switch off.
    /// </remarks>
    [Fact]
    public void Configure_ValidatesTokenLifetime()
    {
        var options = Configure();

        Assert.True(options.TokenValidationParameters.ValidateLifetime);
    }

    /// <remarks>
    /// Secured trigger nodes authorize through <c>ClaimsPrincipal.IsInRole</c>, which resolves roles
    /// via the identity's role claim type. Without these two lines a token's <c>role</c> claims are
    /// never found and every role check silently denies, so the contract is asserted end to end.
    /// </remarks>
    [Fact]
    public void Configure_NamesTheClaimTypesRoleChecksDependOn()
    {
        var options = Configure();

        Assert.Equal(JwtClaimTypes.Name, options.TokenValidationParameters.NameClaimType);
        Assert.Equal(JwtClaimTypes.Role, options.TokenValidationParameters.RoleClaimType);

        var identity = new ClaimsIdentity([new Claim(JwtClaimTypes.Role, "TenantAdmin")],
            JwtBearerDefaults.AuthenticationScheme,
            options.TokenValidationParameters.NameClaimType,
            options.TokenValidationParameters.RoleClaimType);

        Assert.True(new ClaimsPrincipal(identity).IsInRole("TenantAdmin"));
    }

    [Fact]
    public void Configure_RequiresHttpsMetadataOutsideDevelopment()
    {
        Assert.True(Configure().RequireHttpsMetadata);
        Assert.False(Configure(Environments.Development).RequireHttpsMetadata);
    }
}

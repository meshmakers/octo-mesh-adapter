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

    private static IHostEnvironment Environment(string? environmentName = null)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName ?? Environments.Production);

        return environment;
    }

    private static JwtBearerOptions Configure(string? environmentName = null, string? authorityUrl = AuthorityUrl)
    {
        var configuration = Options.Create(new MeshAdapterConfiguration { AuthorityUrl = authorityUrl! });
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(configuration, Environment(environmentName)).Configure(options);

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

    /// <remarks>
    /// A chart that renders the env var without a value overrides the built-in default with an empty
    /// string, which used to reach <c>Authority</c> as "/" and made the JWT handler throw on the first
    /// request through the authentication middleware - answering every request, health probes
    /// included, with HTTP 500 until the deployment rolled back.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("identity.example.com")]
    public void IsAuthorityUsable_RejectsAnythingThatIsNotAnAbsoluteUrl(string? authorityUrl)
    {
        Assert.False(ConfigureJwtBearerOptions.IsAuthorityUsable(authorityUrl, Environment()));
        Assert.False(ConfigureJwtBearerOptions.IsAuthorityUsable(authorityUrl,
            Environment(Environments.Development)));
    }

    [Fact]
    public void IsAuthorityUsable_RejectsHttpOutsideDevelopment()
    {
        Assert.False(ConfigureJwtBearerOptions.IsAuthorityUsable("http://identity.example.com", Environment()));
    }

    /// <remarks>
    /// The compiled-in default. Once the chart stops rendering an empty environment variable, an
    /// unsupplied authority arrives here as this value rather than as blank - and it is a valid
    /// absolute https URL, so a naive check would accept it, leave authentication registered and
    /// this guard silent. The first request carrying a token would then fail fetching discovery
    /// from the pod itself and answer 500 where a denial belongs.
    /// </remarks>
    [Theory]
    [InlineData("https://localhost:5003")]
    [InlineData("https://127.0.0.1:5003")]
    [InlineData("https://[::1]:5003")]
    public void IsAuthorityUsable_RejectsALoopbackAuthorityOutsideDevelopment(string authorityUrl)
    {
        Assert.False(ConfigureJwtBearerOptions.IsAuthorityUsable(authorityUrl, Environment()));
        // The same value is exactly how local development is meant to run.
        Assert.True(ConfigureJwtBearerOptions.IsAuthorityUsable(authorityUrl,
            Environment(Environments.Development)));
    }

    /// <remarks>
    /// <c>RequireHttpsMetadata</c> is switched off in Development, so an identity service reached over
    /// plain HTTP is a legitimate local setup and must not be gated away.
    /// </remarks>
    [Fact]
    public void IsAuthorityUsable_AcceptsHttpInDevelopment()
    {
        Assert.True(ConfigureJwtBearerOptions.IsAuthorityUsable("http://localhost:5003",
            Environment(Environments.Development)));
    }

    [Fact]
    public void IsAuthorityUsable_AcceptsHttps()
    {
        Assert.True(ConfigureJwtBearerOptions.IsAuthorityUsable(AuthorityUrl, Environment()));
    }

    /// <remarks>
    /// Leaving <c>Authority</c> unset is what keeps the handler's post-configure step from building a
    /// metadata address it would reject; the scheme stays registered but validates nothing, so a
    /// secured route sees an anonymous principal and denies it.
    /// </remarks>
    [Fact]
    public void Configure_LeavesTheHandlerUnconfiguredWithoutAUsableAuthority()
    {
        var options = Configure(authorityUrl: string.Empty);

        Assert.Null(options.Authority);
        Assert.Null(options.TokenValidationParameters.ValidIssuer);
        Assert.Null(options.Audience);
    }
}

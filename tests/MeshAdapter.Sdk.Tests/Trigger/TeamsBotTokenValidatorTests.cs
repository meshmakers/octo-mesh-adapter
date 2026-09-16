using System.Security.Claims;
using System.Security.Cryptography;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MeshAdapter.Sdk.Tests.Trigger;

/// <summary>
/// AB#5010: the inbound Bot Framework token check must verify the CRYPTOGRAPHIC signature —
/// the previous payload-only check accepted any self-crafted token with the right audience.
/// These tests sign real JWTs with locally generated RSA keys and feed the validator the
/// matching (or deliberately mismatching) OpenID configuration through its test seam.
/// </summary>
public class TeamsBotTokenValidatorTests
{
    private const string Audience = "11111111-2222-3333-4444-555555555555";
    private const string MetadataUrl = "https://login.botframework.com/v1/.well-known/openidconfiguration";

    private static readonly RsaSecurityKey SigningKey = CreateKey("bot-framework-key");
    private static readonly RsaSecurityKey ForeignKey = CreateKey("someone-elses-key");

    private static RsaSecurityKey CreateKey(string keyId) =>
        new(RSA.Create(2048)) { KeyId = keyId };

    private static OpenIdConnectConfiguration ConfigurationWith(params SecurityKey[] keys)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = TeamsBotTokenValidator.BotFrameworkIssuer };
        foreach (var key in keys)
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }

    private static TeamsBotTokenValidator ValidatorFor(OpenIdConnectConfiguration configuration) =>
        new((_, _) => Task.FromResult(configuration));

    private static string CreateToken(
        SecurityKey signingKey,
        string issuer = TeamsBotTokenValidator.BotFrameworkIssuer,
        string audience = Audience,
        TimeSpan? lifetime = null)
    {
        var handler = new JsonWebTokenHandler();
        var now = DateTime.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(10));
        // Keep nbf < exp even for already-expired tokens, or the handler reports
        // InvalidLifetime (nbf after exp) instead of the expiry we want to test.
        var notBefore = expires - TimeSpan.FromMinutes(30);
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = notBefore,
            Expires = expires,
            Subject = new ClaimsIdentity([new Claim("serviceurl", "https://smba.trafficmanager.net/teams/")]),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    [Fact]
    public async Task Validate_AcceptsAProperlySignedBotFrameworkToken()
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync("Bearer " + CreateToken(SigningKey),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.True(outcome.IsValid);
    }

    /// <remarks>
    /// The regression this work item exists for: a token signed with ANY key used to pass the
    /// payload-only check as long as audience and expiry looked right.
    /// </remarks>
    [Fact]
    public async Task Validate_RejectsATokenSignedWithAForeignKey()
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync("Bearer " + CreateToken(ForeignKey),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public async Task Validate_RejectsAWrongAudience()
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync(
            "Bearer " + CreateToken(SigningKey, audience: "some-other-bot"),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid audience", outcome.Reason);
    }

    [Fact]
    public async Task Validate_RejectsAWrongIssuer()
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync(
            "Bearer " + CreateToken(SigningKey, issuer: "https://evil.example.com"),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid issuer", outcome.Reason);
    }

    [Fact]
    public async Task Validate_RejectsAnExpiredTokenBeyondTheClockSkew()
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync(
            "Bearer " + CreateToken(SigningKey, lifetime: TimeSpan.FromMinutes(-10)),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Equal("token expired", outcome.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwdw==")]
    [InlineData("Bearer not-a-jwt")]
    public async Task Validate_RejectsMissingOrMalformedHeaders(string? authHeader)
    {
        var validator = ValidatorFor(ConfigurationWith(SigningKey));

        var outcome = await validator.ValidateAsync(authHeader, Audience, MetadataUrl, null,
            CancellationToken.None);

        Assert.False(outcome.IsValid);
    }

    /// <remarks>
    /// Fail closed: when the signing keys cannot be fetched, NOTHING can be verified, so the
    /// activity must be rejected rather than let through unverified.
    /// </remarks>
    [Fact]
    public async Task Validate_RejectsWhenTheMetadataIsUnavailable()
    {
        var validator = new TeamsBotTokenValidator(
            (_, _) => Task.FromException<OpenIdConnectConfiguration>(new HttpRequestException("down")));

        var outcome = await validator.ValidateAsync("Bearer " + CreateToken(SigningKey),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("metadata unavailable", outcome.Reason);
    }

    /// <remarks>
    /// Key rollover: the first configuration read misses the token's key, the refresh brings
    /// it — exactly one refresh is requested and the retry succeeds.
    /// </remarks>
    [Fact]
    public async Task Validate_RefreshesTheMetadataOnceOnASigningKeyMiss()
    {
        var calls = 0;
        var refreshes = 0;
        var validator = new TeamsBotTokenValidator(
            (_, _) => Task.FromResult(
                ++calls == 1 ? ConfigurationWith(ForeignKey) : ConfigurationWith(SigningKey)),
            _ => refreshes++);

        var outcome = await validator.ValidateAsync("Bearer " + CreateToken(SigningKey),
            Audience, MetadataUrl, null, CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(1, refreshes);
        Assert.Equal(2, calls);
    }

    /// <remarks>
    /// The default was false ("harden before public exposure"); since AB#5010 the hardening
    /// exists, so shipping validate-off would silently leave endpoints open.
    /// </remarks>
    [Fact]
    public void Configuration_ValidatesInboundTokensByDefault()
    {
        var configuration = new Meshmakers.Octo.MeshAdapter.Nodes.Trigger.FromTeamsBotNodeConfiguration
        {
            ServerConfiguration = "MicrosoftGraphDocuments",
        };

        Assert.True(configuration.ValidateInboundToken);
    }
}

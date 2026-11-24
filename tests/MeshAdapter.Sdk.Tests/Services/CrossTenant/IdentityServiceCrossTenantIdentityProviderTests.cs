using System.Net;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>
///     The default identity provider of cross-tenant access against a scripted token endpoint: the wire shape of the
///     RFC 8693 exchange, how refusals and outages are told apart, and that no credential leaks.
/// </summary>
public class IdentityServiceCrossTenantIdentityProviderTests
{
    private const string Issuer = "https://identity.test";
    private const string CallerToken = "caller-access-token-SECRET";
    private const string AdapterSecret = "adapter-client-secret-SECRET";

    private readonly RecordingLogger _logger = new();

    private static string Jwt(object payload)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"RS256\"}")}.{B64(JsonSerializer.Serialize(payload))}.signature";
    }

    private IdentityServiceCrossTenantIdentityProvider CreateProvider(ScriptedIdentity identity,
        string? clientId = "octo-adapter", string? clientSecret = AdapterSecret)
    {
        return new IdentityServiceCrossTenantIdentityProvider(new HttpClient(identity),
            new AdapterOptions { ClientId = clientId, ClientSecret = clientSecret, IssuerUri = Issuer },
            new MeshAdapterConfiguration(), _logger);
    }

    private static CrossTenantProofRequest UserRequest(string? token = CallerToken) =>
        new("a", "b", CrossTenantSubjectKind.User, RtSecurityContext.ForUser("user-1", ["AdminOfA"]), token, null);

    [Fact]
    public async Task UserExchange_SendsTheRfc8693Request_ToTheOwnIssuer_AndReturnsTheTargetIdentity()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = Jwt(new { sub = "xt_a_user-1", tenant_id = "b", tenant_gen = "gen-7", role = new[] { "ReaderInB" } }),
            token_type = "Bearer",
            expires_in = 300
        }));

        var proof = await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(proof);
        Assert.Equal("b", proof.TenantId);
        Assert.Equal("gen-7", proof.Generation);
        Assert.Equal("xt_a_user-1", proof.SecurityContext.SubjectId);
        // Roles of the target only — never the source caller's.
        Assert.Equal(["ReaderInB"], proof.SecurityContext.Roles);

        var form = identity.LastTokenForm!;
        Assert.Equal(IdentityServiceCrossTenantIdentityProvider.TokenExchangeGrantType, form["grant_type"]);
        Assert.Equal(CallerToken, form["subject_token"]);
        Assert.Equal(IdentityServiceCrossTenantIdentityProvider.AccessTokenTypeIdentifier, form["subject_token_type"]);
        Assert.Equal("tenant:b", form["acr_values"]);
        Assert.StartsWith(Issuer, identity.LastTokenUri!.ToString());
    }

    [Fact]
    public async Task SingleRoleAndNumericGeneration_AreRead()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = Jwt(new { sub = "s", tenant_id = "b", tenant_gen = 638700000000000000L, role = "OnlyRole" }),
            token_type = "Bearer"
        }));

        var proof = await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("638700000000000000", proof!.Generation);
        Assert.Equal(["OnlyRole"], proof.SecurityContext.Roles);
    }

    [Theory]
    [InlineData("{\"error\":\"access_denied\",\"error_description\":\"tenant_not_provisioned: no role\"}")]
    [InlineData("{\"error\":\"invalid_grant\",\"error_description\":\"subject_token is invalid or expired\"}")]
    [InlineData("{\"error\":\"unauthorized_client\"}")]
    public async Task OAuthRefusal_IsNoProof(string body)
    {
        var identity = new ScriptedIdentity(HttpStatusCode.BadRequest, body);

        var proof = await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken);

        Assert.Null(proof);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ServerError_IsUnavailable_NotARefusal()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.InternalServerError, "{}");

        var exception = await Assert.ThrowsAsync<CrossTenantIdentityUnavailableException>(() =>
            CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(CallerToken, exception.ToString());
        Assert.DoesNotContain(AdapterSecret, exception.ToString());
    }

    [Fact]
    public async Task UnreachableIssuer_IsUnavailable()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}") { FailDiscovery = true };

        await Assert.ThrowsAsync<CrossTenantIdentityUnavailableException>(() =>
            CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TokenWithoutTenant_IsNoProof()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = Jwt(new { sub = "s", role = "R" }),
            token_type = "Bearer"
        }));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerWithoutToken_IsNoProof_AndNoRequestIsSent()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}");

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(token: null),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task ServiceAccountSubject_IsNotProvenYet_AndNoRequestIsSent()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}");
        var request = new CrossTenantProofRequest("a", "b", CrossTenantSubjectKind.ServiceAccount,
            RtSecurityContext.ForUser("sa", ["Admin"]), null, null);

        Assert.Null(await CreateProvider(identity).ProveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task AdapterWithoutOwnCredentials_IsUnavailable()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<CrossTenantIdentityUnavailableException>(() =>
            CreateProvider(identity, clientSecret: null).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    private void AssertNoCredentialLogged()
    {
        Assert.DoesNotContain(_logger.Messages, m => m.Contains(CallerToken) || m.Contains(AdapterSecret));
    }

    private sealed class RecordingLogger : ILogger<IdentityServiceCrossTenantIdentityProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    /// <summary>Answers OIDC discovery and the token endpoint; records the posted token form.</summary>
    private sealed class ScriptedIdentity(HttpStatusCode tokenStatus, string tokenBody) : HttpMessageHandler
    {
        public bool FailDiscovery { get; init; }

        public Dictionary<string, string>? LastTokenForm { get; private set; }

        public Uri? LastTokenUri { get; private set; }

        public int TokenCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                if (FailDiscovery)
                {
                    throw new HttpRequestException("connection refused");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["issuer"] = Issuer,
                        ["token_endpoint"] = $"{Issuer}/connect/token",
                        ["jwks_uri"] = $"{Issuer}/.well-known/jwks"
                    }), Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/jwks", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"keys\":[]}", Encoding.UTF8, "application/json")
                };
            }

            TokenCalls++;
            LastTokenUri = request.RequestUri;
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastTokenForm = form.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            return new HttpResponseMessage(tokenStatus)
            {
                Content = new StringContent(tokenBody, Encoding.UTF8, "application/json")
            };
        }
    }
}

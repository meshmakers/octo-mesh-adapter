using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>
///     The default identity provider of cross-tenant access against a scripted identity service that signs its tokens
///     (AB#9767, contract of AB#9777): the wire shape of both RFC 8693 exchanges, the full verification of the issued
///     token (signature, issuer, lifetime, tenant, presenter, subject), how refusals and outages are told apart, and that
///     no credential leaks.
/// </summary>
public class IdentityServiceCrossTenantIdentityProviderTests
{
    private const string Issuer = "https://identity.test";
    private const string CallerToken = "caller-access-token-SECRET";
    private const string AdapterSecret = "adapter-client-secret-SECRET";
    private const string SaClientId = "sa-client";
    private const string ShadowClientId = "xt_a_sa-client";

    private static readonly long Future = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
    private static readonly long Past = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

    private readonly RecordingLogger _logger = new();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly StubSubjectTokenSource _subjectTokens = new();

    public IdentityServiceCrossTenantIdentityProviderTests()
    {
        GivenServiceAccount($$"""{"clientId":"{{SaClientId}}","issuerUri":"","clientSecret":null,"tenantId":null}""");
    }

    private void GivenServiceAccount(params string[] rawJson)
    {
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(
                ServiceAccountConfigurationReader.ServiceAccountConfigurationCkTypeId))
            .Returns(rawJson);
    }

    private IdentityServiceCrossTenantIdentityProvider CreateProvider(ScriptedIdentity identity,
        string? clientId = "octo-adapter", string? clientSecret = AdapterSecret,
        IServiceAccountSubjectTokenSource? subjectTokens = null, bool noSubjectTokens = false)
    {
        return new IdentityServiceCrossTenantIdentityProvider(new HttpClient(identity),
            new AdapterOptions { ClientId = clientId, ClientSecret = clientSecret, IssuerUri = Issuer },
            new MeshAdapterConfiguration(), noSubjectTokens ? null : subjectTokens ?? _subjectTokens, _logger);
    }

    private static CrossTenantProofRequest UserRequest(string? token = CallerToken) =>
        new("a", "b", CrossTenantSubjectKind.User, RtSecurityContext.ForUser("user-1", ["AdminOfA"]), token, null);

    private CrossTenantProofRequest ServiceAccountRequest(IGlobalConfiguration? configuration = null) =>
        new("a", "b", CrossTenantSubjectKind.ServiceAccount, RtSecurityContext.ForUser(SaClientId, ["AdminOfA"]),
            null, configuration ?? _globalConfiguration);

    private static ScriptedIdentity Issues(object payload, bool foreignKey = false) =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = SignedTestJwt.Sign(payload, foreignKey),
            token_type = "Bearer",
            expires_in = 300
        }));

    /// <summary>The issued token of a successful service-account exchange (AB#9777 "Issued access token").</summary>
    private static Dictionary<string, object?> ServiceAccountClaims() => new()
    {
        ["iss"] = Issuer,
        ["client_id"] = ShadowClientId,
        ["tenant_id"] = "b",
        ["tenant_gen"] = "gen-7",
        ["role"] = new[] { "ReaderInB" },
        ["act"] = new { sub = SaClientId },
        ["amr"] = "token_exchange",
        ["exp"] = Future
    };

    private static Dictionary<string, object?> UserClaims() => new()
    {
        ["iss"] = Issuer,
        ["sub"] = "xt_a_user-1",
        ["tenant_id"] = "b",
        ["tenant_gen"] = "gen-7",
        ["role"] = new[] { "ReaderInB" },
        ["exp"] = Future
    };

    private static Dictionary<string, object?> With(Dictionary<string, object?> claims, string name, object? value)
    {
        if (value == null)
        {
            claims.Remove(name);
        }
        else
        {
            claims[name] = value;
        }

        return claims;
    }

    // ---- user caller -------------------------------------------------------------------------------------------

    [Fact]
    public async Task UserExchange_SendsTheRfc8693Request_ToTheOwnIssuer_AndReturnsTheTargetIdentity()
    {
        var identity = Issues(UserClaims());

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
        var identity = Issues(With(With(UserClaims(), "tenant_gen", 638700000000000000L), "role", "OnlyRole"));

        var proof = await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("638700000000000000", proof!.Generation);
        Assert.Equal(["OnlyRole"], proof.SecurityContext.Roles);
    }

    [Theory]
    [InlineData("https://foreign-identity.test")]
    [InlineData(null)]
    public async Task UserExchange_TokenFromAnotherIssuer_IsNoProof(string? issuer)
    {
        // AB#9776 own issuer only, applied to the exchange: a token whose iss is not one of the adapter's own names
        // is never used for the target tenant, even when it is signed, names the target and carries roles.
        var identity = Issues(With(UserClaims(), "iss", issuer));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task UserExchange_BadSignature_IsNoProof()
    {
        var identity = Issues(UserClaims(), foreignKey: true);

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task UserExchange_UnsignedToken_IsNoProof()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = $"{B64("{\"alg\":\"none\"}")}.{B64(JsonSerializer.Serialize(UserClaims()))}.";
        var identity = new ScriptedIdentity(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { access_token = unsigned, token_type = "Bearer" }));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UserExchange_ExpiredToken_IsNoProof()
    {
        var identity = Issues(With(UserClaims(), "exp", Past));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("c")]
    [InlineData("a")]
    [InlineData(null)]
    public async Task UserExchange_TokenForAnotherTenant_IsNoProof(string? tenantId)
    {
        var identity = Issues(With(UserClaims(), "tenant_id", tenantId));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UserExchange_TokenWithoutSubject_IsNoProof()
    {
        var identity = Issues(With(UserClaims(), "sub", null));

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"error\":\"access_denied\",\"error_description\":\"tenant_not_provisioned: no role\"}")]
    [InlineData("{\"error\":\"invalid_grant\",\"error_description\":\"subject_token is invalid or expired\"}")]
    [InlineData("{\"error\":\"unauthorized_client\"}")]
    public async Task UserExchange_OAuthRefusal_IsNoProof(string body)
    {
        var identity = new ScriptedIdentity(HttpStatusCode.BadRequest, body);

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
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
    public async Task CallerWithoutToken_IsNoProof_AndNoRequestIsSent()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}");

        Assert.Null(await CreateProvider(identity).ProveAsync(UserRequest(token: null),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task AdapterWithoutOwnCredentials_IsUnavailable_ForTheUserExchange()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<CrossTenantIdentityUnavailableException>(() =>
            CreateProvider(identity, clientSecret: null).ProveAsync(UserRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    // ---- service account (AB#9777 contract) --------------------------------------------------------------------

    [Fact]
    public async Task ServiceAccountExchange_PresentsTheShadowClient_WithTheAccountsTokenOfT_AndReturnsItsRolesInU()
    {
        var identity = Issues(ServiceAccountClaims());

        var proof = await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(proof);
        Assert.Equal("b", proof.TenantId);
        Assert.Equal("gen-7", proof.Generation);
        Assert.Equal(ShadowClientId, proof.SecurityContext.SubjectId);
        Assert.Equal(["ReaderInB"], proof.SecurityContext.Roles);
        Assert.False(proof.SecurityContext.IsSystem);

        // The subject is the account's own token of T, for the pipeline's tenant.
        var subjectRequest = Assert.Single(_subjectTokens.Requests);
        Assert.Equal(SaClientId, subjectRequest.ClientId);
        Assert.Equal("a", subjectRequest.TenantId);

        var form = identity.LastTokenForm!;
        Assert.Equal(IdentityServiceCrossTenantIdentityProvider.TokenExchangeGrantType, form["grant_type"]);
        Assert.Equal(ShadowClientId, form["client_id"]);
        Assert.Equal(StubSubjectTokenSource.Token, form["subject_token"]);
        Assert.Equal(IdentityServiceCrossTenantIdentityProvider.AccessTokenTypeIdentifier, form["subject_token_type"]);
        Assert.Equal("tenant:b", form["acr_values"]);
        // A public client: no secret, and the adapter's own credentials never go along.
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Null(identity.LastAuthorization);
        Assert.DoesNotContain(AdapterSecret, string.Join("&", form.Values));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ServiceAccountExchange_NeedsNoOwnClientCredentialsOfTheAdapter()
    {
        var identity = Issues(ServiceAccountClaims());

        var proof = await CreateProvider(identity, clientId: null, clientSecret: null)
            .ProveAsync(ServiceAccountRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(proof);
    }

    public static TheoryData<string, string, object?> ServiceAccountTokenDefects => new()
    {
        // claim, value (null = absent), and why the token is no proof
        { "wrong tenant", "tenant_id", "c" },
        { "source tenant", "tenant_id", "a" },
        { "no tenant", "tenant_id", null },
        { "another presenter", "client_id", "xt_a_other-client" },
        { "the account itself", "client_id", SaClientId },
        { "case of the presenter", "client_id", "XT_A_SA-CLIENT" },
        { "no presenter", "client_id", null },
        { "a user subject", "sub", "user-1" },
        { "foreign issuer", "iss", "https://foreign-identity.test" },
        { "no issuer", "iss", null },
        { "expired", "exp", Past },
        { "no expiry", "exp", null }
    };

    [Theory]
    [MemberData(nameof(ServiceAccountTokenDefects))]
    public async Task ServiceAccountExchange_IssuedTokenWithADefect_IsNoProof(string why, string claim, object? value)
    {
        var identity = Issues(With(ServiceAccountClaims(), claim, value));

        var proof = await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken);

        Assert.True(proof == null, why);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ServiceAccountExchange_BadSignature_IsNoProof()
    {
        var identity = Issues(ServiceAccountClaims(), foreignKey: true);

        Assert.Null(await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ServiceAccountExchange_NoSigningKeysPublished_IsNoProof()
    {
        var identity = Issues(ServiceAccountClaims());
        identity.Jwks = "{\"keys\":[]}";

        Assert.Null(await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("access_denied", "tenant_not_provisioned: xt_a_sa-client has no role in b", "tenant_not_provisioned")]
    [InlineData("access_denied", "grant_not_permitted: a does not permit b", "grant_not_permitted")]
    [InlineData("access_denied", "mirrored_client: sa-client", "mirrored_client")]
    [InlineData("access_denied", "shadow_client_invalid: has a secret", "shadow_client_invalid")]
    [InlineData("unauthorized_client", "cross-tenant access from a to b is not allowed", "cross-tenant access")]
    [InlineData("invalid_grant", "earlier generation", "(none)")]
    public async Task ServiceAccountExchange_IdentityRefusal_IsNoProof_AndOnlyCodeAndReasonAreLogged(string error,
        string description, string reason)
    {
        var identity = new ScriptedIdentity(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error, error_description = description }));

        Assert.Null(await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));

        var line = Assert.Single(_logger.Messages, m => m.Contains("refused the cross-tenant exchange"));
        Assert.Contains(error, line);
        Assert.Contains($"({reason})", line);
        Assert.DoesNotContain(description, line);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ServiceAccountExchange_ServerError_IsUnavailable()
    {
        var identity = new ScriptedIdentity(HttpStatusCode.ServiceUnavailable, "{}");

        var exception = await Assert.ThrowsAsync<CrossTenantIdentityUnavailableException>(() =>
            CreateProvider(identity).ProveAsync(ServiceAccountRequest(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(StubSubjectTokenSource.Token, exception.ToString());
    }

    [Fact]
    public async Task ServiceAccountWithoutConfiguration_IsNoProof_AndNoRequestIsSent()
    {
        GivenServiceAccount();
        var identity = Issues(ServiceAccountClaims());

        Assert.Null(await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));
        Assert.Empty(_subjectTokens.Requests);
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task ServiceAccountConfigurationNamingAnotherTenant_IsNoProof_AndNoRequestIsSent()
    {
        // AB#9776: a configuration of T naming U is never the way into U — only the exchange is.
        GivenServiceAccount($$"""{"clientId":"{{SaClientId}}","issuerUri":"","clientSecret":null,"tenantId":"b"}""");
        var identity = Issues(ServiceAccountClaims());

        Assert.Null(await CreateProvider(identity).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));
        Assert.Empty(_subjectTokens.Requests);
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task ServiceAccountWithoutItsOwnToken_IsNoProof_AndNoExchangeIsSent()
    {
        var identity = Issues(ServiceAccountClaims());

        Assert.Null(await CreateProvider(identity, subjectTokens: new StubSubjectTokenSource { Answer = null })
            .ProveAsync(ServiceAccountRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    [Fact]
    public async Task ServiceAccountWithoutTokenSourceOrConfiguration_IsNoProof()
    {
        var identity = Issues(ServiceAccountClaims());

        Assert.Null(await CreateProvider(identity, noSubjectTokens: true).ProveAsync(ServiceAccountRequest(),
            TestContext.Current.CancellationToken));
        Assert.Null(await CreateProvider(identity).ProveAsync(
            new CrossTenantProofRequest("a", "b", CrossTenantSubjectKind.ServiceAccount,
                RtSecurityContext.ForUser(SaClientId, []), null, null), TestContext.Current.CancellationToken));
        Assert.Equal(0, identity.TokenCalls);
    }

    // ---- guard with this provider: generation and error contract ----------------------------------------------

    private CrossTenantAccessGuard GuardWith(IdentityServiceCrossTenantIdentityProvider provider,
        string currentGenerationOfB, ITenantRepository targetRepository)
    {
        var registry = A.Fake<ITenantRegistryReader>();
        A.CallTo(() => registry.GetAsync(A<bool>._, A<CancellationToken>._))
            .Returns(new TenantRegistrySnapshot("octosystem", [("g", null), ("a", "g"), ("b", "g")]));
        var generations = A.Fake<ITenantGenerationReader>();
        A.CallTo(() => generations.GetGenerationAsync("b", A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>(currentGenerationOfB));
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantRepositoryAsync("b")).Returns(targetRepository);
        return new CrossTenantAccessGuard(systemContext, A.Fake<ICkCacheService>(), registry, provider,
            NullLogger<CrossTenantAccessGuard>.Instance, generations);
    }

    private CrossTenantAccessRequest SiblingRequestAsServiceAccount()
    {
        var resolver = A.Fake<IPipelineIdentityResolver>();
        A.CallTo(() => resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.ForUser(SaClientId, ["AdminOfA"])));
        return new CrossTenantAccessRequest("a", new TenantTargetConfiguration
            {
                Relation = TenantRelation.Sibling, TenantId = "b"
            }, NodeExecutionIdentity.ServiceAccount, A.Fake<IDataContext>(), "transformations/0", null, null,
            resolver, _globalConfiguration, new CrossTenantProofCache());
    }

    [Fact]
    public async Task Guard_ServiceAccountProofOfAnEarlierGeneration_IsForbidden_AndTheTargetIsNeverOpened()
    {
        // The token names gen-7, U was recreated meanwhile (gen-8): the proof is worthless (abuse case 8).
        var target = A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());
        var guard = GuardWith(CreateProvider(Issues(ServiceAccountClaims())), "gen-8", target);

        var refused = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            guard.OpenAsync(SiblingRequestAsServiceAccount(), TestContext.Current.CancellationToken));

        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(CrossTenantRefusalReason.TenantGenerationMismatch, refused.ReasonCode);
        A.CallTo(() => ((ISecureSessionFactory)target).GetSessionAsync(A<RtSecurityContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Guard_IdentityRefusal_IsForbidden_WithoutTokenOrDescriptionInTheMessage()
    {
        var target = A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());
        var identity = new ScriptedIdentity(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new
        {
            error = "access_denied", error_description = "tenant_not_provisioned: xt_a_sa-client"
        }));
        var guard = GuardWith(CreateProvider(identity), "gen-7", target);

        var refused = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            guard.OpenAsync(SiblingRequestAsServiceAccount(), TestContext.Current.CancellationToken));

        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        Assert.DoesNotContain(StubSubjectTokenSource.Token, refused.ToString());
        Assert.DoesNotContain("tenant_not_provisioned", refused.Message);
    }

    [Fact]
    public async Task Guard_ServiceAccountProofOfTheCurrentGeneration_OpensTheTarget_WithOnlyTheShadowClientsRoles()
    {
        var target = A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());
        A.CallTo(() => ((ISecureSessionFactory)target).GetSessionAsync(A<RtSecurityContext>._))
            .Returns(Task.FromResult(A.Fake<IOctoSession>()));
        var guard = GuardWith(CreateProvider(Issues(ServiceAccountClaims())), "gen-7", target);

        var access = await guard.OpenAsync(SiblingRequestAsServiceAccount(), TestContext.Current.CancellationToken);
        await access.OpenSessionAsync();

        Assert.Equal("b", access.TenantId);
        A.CallTo(() => ((ISecureSessionFactory)target).GetSessionAsync(A<RtSecurityContext>.That.Matches(c =>
                c.SubjectId == ShadowClientId && c.Roles.SequenceEqual(new[] { "ReaderInB" }))))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("tenant_not_provisioned: x", "tenant_not_provisioned")]
    [InlineData("  grant_not_permitted", "grant_not_permitted")]
    [InlineData("something else tenant_not_provisioned", "(none)")]
    [InlineData(null, "(none)")]
    public void RefusalReason_IsOnlyAKnownKeyword(string? description, string expected)
    {
        Assert.Equal(expected, IdentityServiceCrossTenantIdentityProvider.RefusalReasonOf(description));
    }

    private void AssertNoCredentialLogged()
    {
        Assert.DoesNotContain(_logger.Messages, m => m.Contains(CallerToken) || m.Contains(AdapterSecret)
                                                    || m.Contains(StubSubjectTokenSource.Token)
                                                    || m.Contains("eyJ"));
    }

    private sealed class StubSubjectTokenSource : IServiceAccountSubjectTokenSource
    {
        internal const string Token = "sa-token-of-a-SECRET";

        public string? Answer { get; init; } = Token;

        public List<ServiceAccountCredentials> Requests { get; } = [];

        public Task<string?> AcquireSubjectTokenAsync(ServiceAccountCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(credentials);
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingLogger : ILogger<IdentityServiceCrossTenantIdentityProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    /// <summary>Answers OIDC discovery, the JWKS and the token endpoint; records the posted token form.</summary>
    private sealed class ScriptedIdentity(HttpStatusCode tokenStatus, string tokenBody) : HttpMessageHandler
    {
        public bool FailDiscovery { get; init; }

        public string Jwks { get; set; } = SignedTestJwt.Jwks();

        public Dictionary<string, string>? LastTokenForm { get; private set; }

        public Uri? LastTokenUri { get; private set; }

        public string? LastAuthorization { get; private set; }

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
                    Content = new StringContent(Jwks, Encoding.UTF8, "application/json")
                };
            }

            TokenCalls++;
            LastTokenUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.Scheme;
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

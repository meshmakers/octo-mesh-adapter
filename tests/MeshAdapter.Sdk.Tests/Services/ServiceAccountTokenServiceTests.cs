using System.Net;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
/// Covers <see cref="ServiceAccountTokenService.AcquireDelegatedTokenAsync" /> — the OctoMesh
/// on-behalf-of grant the AI prompt path uses to reach the MCP server as the END USER instead of as
/// a service account with full <c>octo_api</c> reach (AB#5026 / AB#5031).
///
/// The load-bearing assertion is the last one: the delegated token must be RETURNED and must never
/// be written into <see cref="IServiceClientAccessToken" />. That instance is a process-wide
/// singleton and doubles as the adapter's own service identity towards the communication controller,
/// so a user-bound token stored there would leak one caller's identity into every concurrent request.
/// </summary>
public class ServiceAccountTokenServiceTests
{
    private const string Issuer = "https://identity.example.com";
    private const string TokenEndpoint = Issuer + "/connect/token";
    private const string WellKnownName = "ServiceAccountConfig";
    private const string ClientId = "accounting-ai-prompt";
    private const string ClientSecret = "s3cr3t";
    private const string TenantId = "testTenant";
    private const string SubjectToken = "ey.the.callers.token";

    private static readonly RtCkId<CkTypeId> ServiceAccountType =
        new("System.Communication/ServiceAccountConfiguration");

    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IServiceClientAccessToken _serviceClientAccessToken = A.Fake<IServiceClientAccessToken>();

    public ServiceAccountTokenServiceTests()
    {
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(A.Fake<IOctoSession>()));
        SetupConfiguration(tenantId: TenantId);
    }

    private void SetupConfiguration(string? tenantId, string? clientId = ClientId, string? issuer = Issuer)
    {
        var entity = new RtEntity(ServiceAccountType, new OctoObjectId("670000000000000000000042"));
        entity.SetAttributeRawValue("IssuerUri", issuer);
        entity.SetAttributeRawValue("ClientId", clientId);
        entity.SetAttributeRawValue("ClientSecret", ClientSecret);
        entity.SetAttributeRawValue("TenantId", tenantId);

        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity> { entity });
        A.CallTo(() => resultSet.TotalCount).Returns(1);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, ServiceAccountType, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }

    private void SetupNoConfiguration()
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity>());
        A.CallTo(() => resultSet.TotalCount).Returns(0);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, ServiceAccountType, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }

    private ServiceAccountTokenService CreateService(IdentityEndpointHandler handler)
    {
        return new ServiceAccountTokenService(_serviceClientAccessToken,
            NullLogger<ServiceAccountTokenService>.Instance, new HttpClient(handler, disposeHandler: false));
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_SendsTheOnBehalfOfGrantWithTheContractedParameters()
    {
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("delegated-access-token", expiresIn: 300));
        var service = CreateService(handler);

        var token = await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken);

        Assert.Equal("delegated-access-token", token);

        var form = handler.LastTokenForm;
        Assert.NotNull(form);

        // The exact wire contract of OnBehalfOfGrantValidator in octo-identity-services (AB#5026).
        Assert.Equal("urn:meshmakers:params:oauth:grant-type:on-behalf-of", form!["grant_type"]);
        Assert.Equal(SubjectToken, form["subject_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["subject_token_type"]);
        Assert.Equal($"tenant:{TenantId}", form["acr_values"]);
        Assert.Contains("octo_api", form["scope"]);

        // The service account authenticates with its OWN client credentials — the delegation
        // validator only runs after the identity service has proven client_id. IdentityModel's
        // default credential style puts them into the Basic authorization header.
        AssertClientAuthentication(handler, form);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_NeverWritesTheUserBoundTokenIntoTheProcessWideAccessToken()
    {
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("delegated-access-token", expiresIn: 300));
        var service = CreateService(handler);

        var token = await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken);

        Assert.Equal("delegated-access-token", token);
        A.CallToSet(() => _serviceClientAccessToken.AccessToken).MustNotHaveHappened();
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_IdentityRejectsTheGrant_ReturnsNullWithoutTouchingTheServiceIdentity()
    {
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenError(HttpStatusCode.BadRequest,
                """{"error":"invalid_target","error_description":"the subject_token belongs to a different tenant"}"""));
        var service = CreateService(handler);

        var token = await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken);

        Assert.Null(token);
        A.CallToSet(() => _serviceClientAccessToken.AccessToken).MustNotHaveHappened();
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_TokenEndpointUnreachable_ReturnsNullInsteadOfThrowing()
    {
        var handler = new IdentityEndpointHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        var service = CreateService(handler);

        Assert.Null(await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken));
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_DiscoveryThrows_ReturnsNullInsteadOfPropagating()
    {
        // AB#4541 shape: a placeholder IssuerUri left behind by a blueprint re-apply makes OIDC
        // discovery THROW ("Malformed URL") rather than report an error. The method promises null
        // on failure, so the caller — not this method — decides that it is fatal.
        SetupConfiguration(tenantId: TenantId, issuer: "https://identity.invalid");
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("must-not-be-issued", expiresIn: 300))
        {
            FailDiscovery = true
        };
        var service = CreateService(handler);

        Assert.Null(await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_NoSubjectToken_ReturnsNullWithoutCallingIdentity()
    {
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("must-not-be-issued", expiresIn: 300));
        var service = CreateService(handler);

        Assert.Null(await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, string.Empty));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_ConfigurationWithoutTenantId_ReturnsNull()
    {
        // The grant is same-tenant and the identity service needs acr_values=tenant:X to wire the
        // request to a tenant at all — without one, the request would be rejected with a message
        // that reads like an outage rather than a misconfiguration.
        SetupConfiguration(tenantId: null);
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("must-not-be-issued", expiresIn: 300));
        var service = CreateService(handler);

        Assert.Null(await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_ConfigurationMissing_ReturnsNull()
    {
        SetupNoConfiguration();
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("must-not-be-issued", expiresIn: 300));
        var service = CreateService(handler);

        Assert.Null(await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EnsureTokenAsync_StillWritesTheServiceIdentity()
    {
        // Regression guard for the additive change: DeployPipelineNode and the non-delegating MCP
        // path depend on EnsureTokenAsync writing the process-wide access token.
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("service-account-token", expiresIn: 300));
        var service = CreateService(handler);

        await service.EnsureTokenAsync(_tenantRepository, WellKnownName);

        A.CallToSet(() => _serviceClientAccessToken.AccessToken).To("service-account-token")
            .MustHaveHappenedOnceExactly();
        Assert.Equal("client_credentials", handler.LastTokenForm!["grant_type"]);
    }

    // ---------------------------------------------------------------------------------------------
    // AcquireServiceAccountIdentityAsync (AB#5028): who the service account IS. Roles are not on the
    // ServiceAccountConfiguration entity — the only place they exist is the issued token's claims.
    // ---------------------------------------------------------------------------------------------

    private static readonly ServiceAccountCredentials Credentials =
        new(Issuer, ClientId, ClientSecret, TenantId);

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_ReadsSubjectAndRolesOutOfTheIssuedToken()
    {
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId,
                roles: ["CommunicationManagement", "Accounting"], expiresInSeconds: 3600),
            expiresIn: 3600));
        var service = CreateService(handler);

        var identity = await service.AcquireServiceAccountIdentityAsync(Credentials);

        Assert.NotNull(identity);
        // A client-credentials token carries no 'sub'; client_id is the subject the engine stamps and
        // filters on — the same precedence the MCP server's resolver uses.
        Assert.Equal(ClientId, identity!.SubjectId);
        Assert.Equal(["CommunicationManagement", "Accounting"], identity.Roles);
        Assert.Equal("client_credentials", handler.LastTokenForm!["grant_type"]);
        Assert.Equal($"tenant:{TenantId}", handler.LastTokenForm["acr_values"]);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_ASingleRoleIsNotAnArray()
    {
        // A JWT emits one role as a bare string and several as an array. Reading only the array shape
        // would make an account with exactly one role look role-less — which fails silently.
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId, roles: ["Accounting"], expiresInSeconds: 3600),
            expiresIn: 3600));

        var identity = await CreateService(handler).AcquireServiceAccountIdentityAsync(Credentials);

        Assert.Equal(["Accounting"], identity!.Roles);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_ASubjectClaimWins()
    {
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: "sub-42", clientId: ClientId, roles: [], expiresInSeconds: 3600),
            expiresIn: 3600));

        var identity = await CreateService(handler).AcquireServiceAccountIdentityAsync(Credentials);

        Assert.Equal("sub-42", identity!.SubjectId);
        Assert.Empty(identity.Roles);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_SecondCallIsAnsweredFromTheCache()
    {
        // Every scoped session of every execution asks for this. A round trip per pipeline run would
        // be paid by exactly the high-frequency event triggers the lazy resolution exists to protect.
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId, roles: ["Accounting"], expiresInSeconds: 3600),
            expiresIn: 3600));
        var service = CreateService(handler);

        var first = await service.AcquireServiceAccountIdentityAsync(Credentials);
        var second = await service.AcquireServiceAccountIdentityAsync(Credentials);

        Assert.Equal(first!.SubjectId, second!.SubjectId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_ADifferentClientIsNotServedFromAnotherCacheEntry()
    {
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId, roles: ["Accounting"], expiresInSeconds: 3600),
            expiresIn: 3600));
        var service = CreateService(handler);

        await service.AcquireServiceAccountIdentityAsync(Credentials);
        await service.AcquireServiceAccountIdentityAsync(Credentials with { ClientId = "another-client" });

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_AnAlmostExpiredTokenIsNotCached()
    {
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId, roles: ["Accounting"], expiresInSeconds: 1),
            expiresIn: 1));
        var service = CreateService(handler);

        await service.AcquireServiceAccountIdentityAsync(Credentials);
        await service.AcquireServiceAccountIdentityAsync(Credentials);

        // The cached entry expires a minute before the token does, so a one-second token is never reused.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_AnOpaqueTokenYieldsNoIdentity()
    {
        // Inventing an empty-role identity would look like a correctly resolved account with no
        // permissions — the failure mode that goes unnoticed everywhere downstream.
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("an-opaque-reference-token", expiresIn: 3600));

        Assert.Null(await CreateService(handler).AcquireServiceAccountIdentityAsync(Credentials));
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_IdentityRefusesTheClient_ReturnsNull()
    {
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenError(
            HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""));

        Assert.Null(await CreateService(handler).AcquireServiceAccountIdentityAsync(Credentials));
    }

    [Fact]
    public async Task AcquireServiceAccountIdentityAsync_NeverTouchesTheProcessWideServiceIdentity()
    {
        // This call answers an identity question; it does not hand out a credential, and it must not
        // overwrite the adapter's own service token towards the communication controller.
        var handler = new IdentityEndpointHandler(IdentityEndpointHandler.TokenResponse(
            TestJwt.Create(subject: null, clientId: ClientId, roles: ["Accounting"], expiresInSeconds: 3600),
            expiresIn: 3600));

        await CreateService(handler).AcquireServiceAccountIdentityAsync(Credentials);

        A.CallToSet(() => _serviceClientAccessToken.AccessToken).MustNotHaveHappened();
    }

    // ---------------------------------------------------------------------------------------------
    // AB#5029: the semantics of the SA ∩ user intersection, on the adapter side of the delegation
    // grant. The intersection itself is computed by the identity service (AB#5026); what the adapter
    // owns is how it reacts to the result — and the load-bearing part of that is the degenerate case.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AcquireDelegatedTokenAsync_AnEmptyRoleIntersectionIsASuccess_TheTokenSimplyCarriesNoRoles()
    {
        // 🔴 THE property this test exists to protect. An empty intersection — a caller and a service
        // account with no role in common — is not an error condition anywhere: the identity service
        // issues a perfectly valid token that happens to carry no role claims, and the only symptom is
        // that the delegated call returns nothing. That IS the fail-closed behaviour, and it is what
        // makes the mode safe.
        //
        // The tempting "repair" is to notice the empty role set here and treat it as a failed
        // acquisition. That must never happen: AnthropicAiQueryNode turns a null token into a hard
        // failure (AnthropicAiQueryNodeDelegationTests), so rejecting a role-less token would turn a
        // correctly restricted answer ("you may not see anything") into an outage ("the assistant is
        // broken") — and the pressure to then relax it back towards the service account's own reach
        // is exactly how a delegation feature loses its point.
        var roleless = TestJwt.Create(subject: "user-42", clientId: null, roles: [], expiresInSeconds: 300);
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse(roleless, expiresIn: 300));
        var service = CreateService(handler);

        var token = await service.AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken);

        Assert.Equal(roleless, token);

        // And it is handed back verbatim — no substitution, no enrichment from the service account.
        Assert.True(JwtPayloadReader.TryRead(token, out var claims));
        Assert.Empty(claims.Roles);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_TheSubjectStaysTheCaller_NotTheServiceAccount()
    {
        // AB#5029, rule 2: the issued token runs on the CALLER's sub. Owner-scoped checks downstream
        // (RtCreatedBy, ownerAttributePath — AB#4978) therefore ask about the human, not about the
        // account acting for them. The adapter must not rewrite that, and it must not fall back to
        // its own client id when the token has one.
        var delegated = TestJwt.Create(subject: "user-42", clientId: ClientId,
            roles: ["Accounting"], expiresInSeconds: 300);
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse(delegated, expiresIn: 300));

        var token = await CreateService(handler).AcquireDelegatedTokenAsync(
            _tenantRepository, WellKnownName, SubjectToken);

        Assert.True(JwtPayloadReader.TryRead(token, out var claims));
        Assert.Equal("user-42", claims.Subject);
        Assert.NotEqual(ClientId, claims.Subject);
        Assert.Equal(["Accounting"], claims.Roles);

        // The caller's own token went out as subject_token — the intersection is over the two
        // parties the grant names, and the caller is one of them by presenting exactly this.
        Assert.Equal(SubjectToken, handler.LastTokenForm!["subject_token"]);
    }

    [Fact]
    public async Task AcquireDelegatedTokenAsync_NeverRequestsOfflineAccess()
    {
        // The intersection is computed at issuance, so a refresh token would freeze it and keep a
        // revoked role alive for the lifetime of the refresh. The identity service rejects the scope
        // outright for that reason, so asking would fail the whole request.
        var handler = new IdentityEndpointHandler(
            IdentityEndpointHandler.TokenResponse("delegated-access-token", expiresIn: 300));

        await CreateService(handler).AcquireDelegatedTokenAsync(_tenantRepository, WellKnownName, SubjectToken);

        Assert.DoesNotContain("offline_access", handler.LastTokenForm!["scope"], StringComparison.Ordinal);
    }

    /// <summary>Builds the compact-serialisation JWTs the identity stub hands out.</summary>
    private static class TestJwt
    {
        public static string Create(string? subject, string? clientId, string[] roles, int expiresInSeconds)
        {
            var claims = new List<string>
            {
                $"\"exp\":{DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds()}"
            };

            if (subject != null)
            {
                claims.Add($"\"sub\":\"{subject}\"");
            }

            if (clientId != null)
            {
                claims.Add($"\"client_id\":\"{clientId}\"");
            }

            if (roles.Length == 1)
            {
                claims.Add($"\"role\":\"{roles[0]}\"");
            }
            else if (roles.Length > 1)
            {
                claims.Add($"\"role\":[{string.Join(",", roles.Select(r => $"\"{r}\""))}]");
            }

            return $"{Segment("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}."
                   + $"{Segment("{" + string.Join(",", claims) + "}")}.signature-not-verified";
        }

        private static string Segment(string json)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
    /// <summary>
    /// Asserts that the request carried the service account's own client credentials, whichever of
    /// the two OAuth-sanctioned places IdentityModel put them in (Basic header or post body).
    /// </summary>
    private static void AssertClientAuthentication(IdentityEndpointHandler handler,
        IReadOnlyDictionary<string, string> form)
    {
        if (form.TryGetValue("client_id", out var postedClientId))
        {
            Assert.Equal(ClientId, postedClientId);
            Assert.Equal(ClientSecret, form["client_secret"]);
            return;
        }

        var authorization = handler.LastTokenAuthorization;
        Assert.NotNull(authorization);
        Assert.Equal("Basic", authorization!.Scheme);

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!));
        var separator = decoded.IndexOf(':');
        Assert.Equal(ClientId, Uri.UnescapeDataString(decoded[..separator]));
        Assert.Equal(ClientSecret, Uri.UnescapeDataString(decoded[(separator + 1)..]));
    }

    /// <summary>
    /// Answers the OIDC discovery document itself (IdentityModel always fetches it first) and hands
    /// every token request to the scripted step, recording the posted form so the wire contract can
    /// be asserted.
    /// </summary>
    private sealed class IdentityEndpointHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> tokenStep)
        : HttpMessageHandler
    {
        public Dictionary<string, string>? LastTokenForm { get; private set; }

        /// <summary>The <c>Authorization</c> header of the last token request, if any.</summary>
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastTokenAuthorization { get; private set; }

        /// <summary>Token requests only — the discovery fetch is not counted.</summary>
        public int CallCount { get; private set; }

        /// <summary>Makes the discovery fetch throw the way an unreachable/malformed issuer does.</summary>
        public bool FailDiscovery { get; init; }

        public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> TokenResponse(
            string accessToken, int expiresIn)
        {
            return (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":{{expiresIn}}}""",
                    Encoding.UTF8, "application/json")
            });
        }

        public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> TokenError(
            HttpStatusCode statusCode, string body)
        {
            return (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                if (FailDiscovery)
                {
                    throw new InvalidOperationException("Malformed URL");
                }

                if (path.EndsWith("/jwks", StringComparison.Ordinal))
                {
                    return Json("""{"keys":[]}""");
                }

                return Json($$"""
                              {
                                "issuer": "{{Issuer}}",
                                "token_endpoint": "{{TokenEndpoint}}",
                                "jwks_uri": "{{Issuer}}/.well-known/openid-configuration/jwks"
                              }
                              """);
            }

            CallCount++;
            LastTokenAuthorization = request.Headers.Authorization;
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                LastTokenForm = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                        parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);
            }

            return await tokenStep(request, cancellationToken);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

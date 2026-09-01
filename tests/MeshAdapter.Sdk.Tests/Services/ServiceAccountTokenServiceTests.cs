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

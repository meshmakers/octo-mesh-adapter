using System.Net;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLog.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#4924 increment 6, entry criterion 4 — <b>the identity assertion</b>.
/// </summary>
/// <remarks>
///     <para>
///         🔴 The property is not "a token exists". It is "<b>this</b> token belongs to the tenant the
///         lease named". Since AB#5077 a client-credentials request that lost its
///         <c>acr_values=tenant:X</c> still returns <c>200</c> with a perfectly valid token — for the
///         <i>system</i> tenant. A member that acted on it would be a 403 if it were lucky and a
///         cross-tenant read if it were not, and every "did we get a token" assertion in the world
///         would be green.
///     </para>
///     <para>
///         So the participant verifies the token's own <c>tenant_id</c> claim and <b>fails the
///         lease</b> on a mismatch. That is production behaviour; these tests observe it.
///     </para>
/// </remarks>
public class BorrowerIdentityLeaseParticipantTests
{
    private const string Issuer = "https://identity.example.com";
    private const string BorrowerTenantId = "tenant-b";
    private const string BorrowerClientId = "octo-pipeline-sa-tenant-b";
    private const string BorrowerSecret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm";

    private readonly IServiceClientAccessToken _accessToken = new ServiceClientAccessToken();

    private static LeaseDto ALease(string tenantId = BorrowerTenantId) => new()
    {
        LeaseId = "lease-1",
        TenantId = tenantId,
        PoolTenantId = "lender",
        PoolRtId = "665f0000000000000000ee21",
        AdapterRtId = "665f0000000000000000ee22",
        AdapterCkTypeId = "System.Communication/Adapter",
        ClientId = BorrowerClientId,
        ClientSecret = BorrowerSecret,
        GrantedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };

    /// <summary>A JWT the fake identity service issues. Only the payload is read — see JwtPayloadReader.</summary>
    private static string Jwt(string? tenantId, string clientId = BorrowerClientId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        if (tenantId is not null)
        {
            payload["tenant_id"] = tenantId;
        }

        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJub25lIn0.{encoded}.signature";
    }

    private BorrowerIdentityLeaseParticipant CreateParticipant(TokenEndpointHandler handler)
    {
        return new BorrowerIdentityLeaseParticipant(_accessToken,
            new HttpClient(handler, disposeHandler: false),
            new AdapterOptions { IssuerUri = Issuer },
            new MeshAdapterConfiguration(),
            NullLogger<BorrowerIdentityLeaseParticipant>.Instance);
    }

    /// <summary>
    ///     🔴 The request must carry the borrower's own credential <b>and</b>
    ///     <c>acr_values=tenant:{borrower}</c>. Without the latter the identity service issues for the
    ///     system tenant.
    /// </summary>
    [Fact]
    public async Task TheTokenIsRequestedForTheBorrowerTenantWithTheBorrowersCredential()
    {
        var handler = new TokenEndpointHandler(Jwt(BorrowerTenantId));
        var participant = CreateParticipant(handler);

        await participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        Assert.NotNull(handler.LastForm);
        Assert.Equal("client_credentials", handler.LastForm!["grant_type"]);
        Assert.Equal($"tenant:{BorrowerTenantId}", handler.LastForm["acr_values"]);
        // The credential travels in the Authorization header, not the form — that is the default
        // client-credential style of the token client every other path in this adapter uses too.
        Assert.Equal((BorrowerClientId, BorrowerSecret), handler.LastBasicCredential);
    }

    /// <summary>
    ///     🔴 Entry criterion 4, stated directly: the token presented while tenant B is leased carries
    ///     <c>tenant_id=B</c>.
    /// </summary>
    [Fact]
    public async Task ThePublishedTokenCarriesTheBorrowersTenantId()
    {
        var handler = new TokenEndpointHandler(Jwt(BorrowerTenantId));
        var participant = CreateParticipant(handler);

        await participant.EnterLeaseAsync(ALease(), CancellationToken.None);

        Assert.True(Meshmakers.Octo.Sdk.MeshAdapter.Services.JwtPayloadReader
            .TryRead(_accessToken.AccessToken, out var claims));
        Assert.Equal(BorrowerTenantId, claims.TenantId);
    }

    /// <summary>
    ///     🔴 The AB#5077 failure mode. A token with no <c>tenant_id</c> is a system-tenant token; the
    ///     lease is refused rather than run under it.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutATenantClaim_FailsTheLease()
    {
        var handler = new TokenEndpointHandler(Jwt(tenantId: null));
        var participant = CreateParticipant(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => participant.EnterLeaseAsync(ALease(), CancellationToken.None));

        Assert.Contains("SYSTEM tenant", exception.Message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(_accessToken.AccessToken));
    }

    /// <summary>
    ///     🔴 And the one that would be a genuine cross-tenant read: a valid token belonging to some
    ///     other tenant. Refused, and never published.
    /// </summary>
    [Fact]
    public async Task ATokenOfAnotherTenant_FailsTheLease()
    {
        var handler = new TokenEndpointHandler(Jwt("somebody-else"));
        var participant = CreateParticipant(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => participant.EnterLeaseAsync(ALease(), CancellationToken.None));

        Assert.Contains("belongs to tenant 'somebody-else'", exception.Message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(_accessToken.AccessToken));
    }

    /// <summary>
    ///     🔴 On release the member presents <b>no</b> credential at all — not its own, not the
    ///     previous borrower's. Leaving a token behind is the exact shape of a cross-tenant call on
    ///     the next lease.
    /// </summary>
    [Fact]
    public async Task LeavingTheLeaseEmptiesTheTokenHolder()
    {
        var handler = new TokenEndpointHandler(Jwt(BorrowerTenantId));
        var participant = CreateParticipant(handler);
        await participant.EnterLeaseAsync(ALease(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(_accessToken.AccessToken));

        await participant.LeaveLeaseAsync(ALease(), CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(_accessToken.AccessToken));
    }

    /// <summary>
    ///     A refusal from the identity service names the client, never the secret — the same rule the
    ///     controller's deploy path follows.
    /// </summary>
    [Fact]
    public async Task ARefusedCredential_FailsTheLeaseWithoutNamingTheSecret()
    {
        var handler = new TokenEndpointHandler(accessToken: null, error: "invalid_client");
        var participant = CreateParticipant(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => participant.EnterLeaseAsync(ALease(), CancellationToken.None));

        Assert.Contains(BorrowerClientId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BorrowerSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BorrowerSecret[..8], exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     🔴 The lease credential must not reach a log target — the same guard AB#5027 put on the
    ///     deploy path, applied to the second route concept §8 Q6 opened.
    /// </summary>
    [Fact]
    public async Task TheBorrowerSecretNeverReachesALogTarget()
    {
        var handler = new TokenEndpointHandler(Jwt(BorrowerTenantId));
        var participant = CreateParticipant(handler);

        var memoryTarget = new NLog.Targets.MemoryTarget("lease-identity-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            // A logger that actually writes, so the probe is not vacuous: the NullLogger the other
            // tests use would make any assertion about log content meaningless.
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddNLog();
            });
            var loggingParticipant = new BorrowerIdentityLeaseParticipant(_accessToken,
                new HttpClient(handler, disposeHandler: false),
                new AdapterOptions { IssuerUri = Issuer },
                new MeshAdapterConfiguration(),
                loggerFactory.CreateLogger<BorrowerIdentityLeaseParticipant>());

            await loggingParticipant.EnterLeaseAsync(ALease(), CancellationToken.None);
            await loggingParticipant.LeaveLeaseAsync(ALease(), CancellationToken.None);

            // The value really did travel — otherwise the probe proves nothing.
            Assert.Equal((BorrowerClientId, BorrowerSecret), handler.LastBasicCredential);
            Assert.NotEmpty(memoryTarget.Logs);
            Assert.DoesNotContain(memoryTarget.Logs, l => l.Contains(BorrowerSecret, StringComparison.Ordinal));
            Assert.DoesNotContain(memoryTarget.Logs,
                l => l.Contains(BorrowerSecret[..8], StringComparison.Ordinal));
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    /// <summary>
    ///     A fake identity service: OIDC discovery plus one token endpoint that records the form it
    ///     received.
    /// </summary>
    private sealed class TokenEndpointHandler(string? accessToken, string? error = null) : HttpMessageHandler
    {
        public Dictionary<string, string>? LastForm { get; private set; }

        /// <summary>The client id / secret decoded out of the Basic authorization header.</summary>
        public (string ClientId, string ClientSecret)? LastBasicCredential { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var authority = $"{request.RequestUri.Scheme}://{request.RequestUri.Authority}";
                return Json($$"""
                              {
                                "issuer": "{{Issuer}}",
                                "token_endpoint": "{{authority}}/connect/token",
                                "jwks_uri": "{{authority}}/.well-known/openid-configuration/jwks"
                              }
                              """);
            }

            if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } parameter })
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter)).Split(':', 2);
                LastBasicCredential = (Uri.UnescapeDataString(decoded[0]),
                    Uri.UnescapeDataString(decoded.Length > 1 ? decoded[1] : string.Empty));
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastForm = body.Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                    parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : string.Empty));

            if (error is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent($$"""{"error":"{{error}}"}""", Encoding.UTF8,
                        "application/json")
                };
            }

            return Json($$"""
                          {"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":3600}
                          """);
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
